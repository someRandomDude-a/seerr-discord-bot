extern alias Integration;
using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using MediaHub.Tests;
using ActivityController = Integration::Jellyfin.Plugin.MediaHub.ActivityController;
using AdapterSettings = Integration::Jellyfin.Plugin.MediaHub.SettingsService;
using AdapterRpc = Integration::Jellyfin.Plugin.MediaHub.Rpc;
using AdapterBoundary = Integration::Jellyfin.Plugin.MediaHub.HubRequestBoundary;
using Jellyfin.Plugin.WatchlistDeletion.Api;

namespace MediaHub.Discord.Tests;

/// <summary>Real loopback HTTP/TLS and MVC, synthetic native identity/OAuth only.
/// No certificate-store edits, host configuration changes or public origin required.</summary>
public sealed class LoopbackOriginTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/mediahub")]
    public async Task SecureCookieAndCallbackStayOnConfiguredOriginAndRequireTheSameBrowser(string basePath)
    {
        await using var host = await OriginHost.Start(basePath);
        using var browser = host.Client(); using var crawler = host.Client();
        var handoff = host.Fixture.Store.BeginLink("42"); var initial = host.Fixture.Store.Credential("42");
        using var begin = await browser.PostAsJsonAsync(host.Https + "/MediaHub/Link/Handoff/Begin", new { Code = handoff });
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var cookie = Assert.Single(begin.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase); Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=" + basePath + "/MediaHub/Link/OAuth/Callback", cookie, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var authorize = new Uri(body.RootElement.GetProperty("AuthorizeUrl").GetString()!);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(host.Https + "/MediaHub/Link/OAuth/Callback", query["redirect_uri"].ToString());
        Assert.Equal("https", authorize.Scheme); Assert.Equal("discord.com", authorize.Host);
        var state = query["state"].ToString();
        var callback = host.Https + "/MediaHub/Link/OAuth/Callback?state=" + state + "&code=synthetic-one-use-code";
        using (var page = await crawler.GetAsync(callback))
        {
            Assert.Contains("No account was linked", await page.Content.ReadAsStringAsync()); Assert.Equal(0, host.OAuth.Exchanges);
            Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
            Assert.Contains("frame-ancestors 'none'", Assert.Single(page.Headers.GetValues("Content-Security-Policy")));
        }
        using (var page = await browser.GetAsync(callback))
        {
            Assert.Contains("Discord verified", await page.Content.ReadAsStringAsync()); Assert.Equal(1, host.OAuth.Exchanges);
            Assert.Equal(initial, host.Fixture.Store.Credential("42")); // GET proves, never links.
        }
        using (var replay = await browser.GetAsync(callback)) Assert.Contains("No account was linked", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, host.OAuth.Exchanges); Assert.Equal(1, host.OAuth.Revocations);
        using var complete = await browser.PostAsJsonAsync(host.Https + "/MediaHub/Link/Handoff/Complete", new { Code = handoff });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode); Assert.Equal(host.Fixture.User.Id, host.Fixture.Store.LinkedUser("42"));
        host.Fixture.Data.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/mediahub")]
    public async Task OriginAllowlistAndUntrustedForwardedHeadersCannotSelectOAuthDestination(string basePath)
    {
        await using var host = await OriginHost.Start(basePath); using var client = host.Client();
        var authority = new Uri(host.Https).GetLeftPart(UriPartial.Authority);
        foreach (var origin in new[] { authority, "https://123456.discordsays.com", "null", authority + "/path", authority + ".invalid", "https://999.discordsays.com", "http://123456.discordsays.com" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host.Https + "/MediaHub/Activity/Config"); request.Headers.Add("Origin", origin);
            using var response = await client.SendAsync(request);
            Assert.Equal(origin == authority || origin == "https://123456.discordsays.com" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        }
        var handoff = host.Fixture.Store.BeginLink("42");
        using var forged = new HttpRequestMessage(HttpMethod.Post, host.Http + "/MediaHub/Link/Handoff/Begin") { Content = JsonContent.Create(new { Code = handoff }) };
        forged.Headers.Add("X-Forwarded-Proto", "https"); forged.Headers.Add("X-Forwarded-Host", new Uri(host.Https).Authority);
        forged.Headers.Add("Forwarded", "host=attacker.invalid;proto=https");
        using var denied = await client.SendAsync(forged); Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.False(denied.Headers.Contains("Set-Cookie"));
        using var wrongHost = new HttpRequestMessage(HttpMethod.Post, host.Https + "/MediaHub/Link/Handoff/Begin") { Content = JsonContent.Create(new { Code = handoff }) };
        wrongHost.Headers.Host = "attacker.invalid";
        using var rejected = await client.SendAsync(wrongHost); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Set-Cookie")); Assert.Equal(0, host.OAuth.Exchanges);
        // A malformed plugin request must leave the same HTTP host healthy and private state intact.
        var settings = File.ReadAllBytes(Path.Combine(host.Fixture.Directory, "settings.json"));
        using var malformed = await client.PostAsync(host.Https + "/MediaHub/Link/Handoff/Begin", new StringContent("{\"Code\":\"a\",\"code\":\"b\"}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var healthy = await client.GetAsync(host.Https + "/MediaHub/Activity/Config"); Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(host.Fixture.Directory, "settings.json")));
    }

    [Fact]
    public async Task ExplicitlyTrustedLoopbackProxyPreservesConfiguredCallbackAndRejectsAnotherHost()
    {
        await using var host = await OriginHost.Start("/proxy-base", proxy: true); using var client = host.Client();
        var handoff = host.Fixture.Store.BeginLink("42");
        foreach (var forwardedHost in new[] { new Uri(host.Https).Authority, "attacker.invalid" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, host.Http + "/MediaHub/Link/Handoff/Begin") { Content = JsonContent.Create(new { Code = handoff }) };
            request.Headers.Add("X-Forwarded-Proto", "https"); request.Headers.Add("X-Forwarded-Host", forwardedHost);
            using var response = await client.SendAsync(request);
            if (forwardedHost == "attacker.invalid")
            { Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.False(response.Headers.Contains("Set-Cookie")); }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var authorize = new Uri(body.RootElement.GetProperty("AuthorizeUrl").GetString()!);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
                Assert.Equal(host.Https + "/MediaHub/Link/OAuth/Callback", query["redirect_uri"].ToString());
                Assert.Contains("path=/proxy-base/MediaHub/Link/OAuth/Callback", Assert.Single(response.Headers.GetValues("Set-Cookie")));
            }
        }
        Assert.Equal(0, host.OAuth.Exchanges); // Proxy simulation never contacts a real provider.
    }

    [Fact]
    public async Task TestCertificateIsNotTrustedWithoutExplicitPerClientPinning()
    {
        await using var host = await OriginHost.Start("");
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(host.Https + "/MediaHub/Activity/Config"));
        Assert.True(error.InnerException is AuthenticationException); // No global trust-store bypass.
    }

    private sealed class OriginHost(FeatureFixture fixture, WebApplication app, X509Certificate2 certificate, SyntheticOAuth oauth, string http, string https) : IAsyncDisposable
    {
        internal FeatureFixture Fixture => fixture;
        internal SyntheticOAuth OAuth => oauth;
        internal string Http => http;
        internal string Https => https;
        internal HttpClient Client() => new(new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.RawData.SequenceEqual(certificate.RawData) == true
        }) { Timeout = TimeSpan.FromSeconds(15) };
        internal static async Task<OriginHost> Start(string basePath, bool proxy = false)
        {
            var fixture = new FeatureFixture();
            using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=Media Hub disposable loopback test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Windows Schannel cannot use ephemeral imported server private keys.
            // DefaultKeySet's temporary key is removed on disposal; no certificate
            // is installed in a machine/user trust store, and PersistKeySet is never used.
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => { server.Listen(IPAddress.Loopback, 0); server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)); });
            var oauth = new SyntheticOAuth(); builder.Services.AddSingleton(fixture.Settings);
            builder.Services.AddSingleton<CoreApiProvider>();
            builder.Services.AddKeyedSingleton<Func<string, string, string, CancellationToken, Task<string>>>(CoreApi.ServiceKey, (provider, _) => provider.GetRequiredService<CoreApiProvider>().Invoke);
            builder.Services.AddWatchlistDeletionClient(); builder.Services.AddSingleton<AdapterRpc>(); builder.Services.AddSingleton<AdapterSettings>(); builder.Services.AddSingleton<AdapterBoundary>();
            builder.Services.AddSingleton(new LinkFlow(fixture.Store, fixture.Settings, fixture.Identity, fixture.Bridge, oauth));
            builder.Services.AddSingleton<HubRequestBoundary>(); builder.Services.AddSingleton(fixture);
            builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null).AddApplicationPart(typeof(LinkController).Assembly).AddApplicationPart(typeof(ActivityController).Assembly);
            builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, NativeFixtureAuthentication>("Fixture", _ => { }); builder.Services.AddAuthorization();
            var app = builder.Build();
            if (proxy)
            {
                // Test host only: the plugin must not alter Jellyfin's trust/proxy policy.
                var forwarding = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, ForwardLimit = 1 };
                forwarding.KnownIPNetworks.Clear(); forwarding.KnownProxies.Clear(); forwarding.KnownProxies.Add(IPAddress.Loopback);
                app.UseForwardedHeaders(forwarding);
            }
            if (basePath.Length > 0) app.UsePathBase(basePath);
            app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
            try
            {
                await app.StartAsync();
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
                var http = addresses.Single(url => url.StartsWith("http:", StringComparison.Ordinal)) + basePath;
                var https = addresses.Single(url => url.StartsWith("https:", StringComparison.Ordinal)) + basePath;
                var s = fixture.Settings.Current;
                fixture.Settings.Update(new(s.Revision, s.DiscordEnabled, https, s.AllowedGuilds, s.SeerrUrl, null, null,
                    Features: fixture.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "synthetic-secret", ActivityEnabled = true }));
                return new(fixture, app, certificate, oauth, http, https);
            }
            catch { await app.DisposeAsync(); certificate.Dispose(); fixture.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); certificate.Dispose(); fixture.Dispose(); }
    }
    private sealed class NativeFixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, FeatureFixture fixture)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
            new("Jellyfin-UserId", fixture.User.Id.ToString()), new("Jellyfin-DeviceId", "device"), new("Jellyfin-IsApiKey", "False"), new("Jellyfin-Token", "token")], Scheme.Name)), Scheme.Name)));
    }
    private sealed class SyntheticOAuth : IDiscordOAuth
    {
        internal int Exchanges, Revocations;
        public Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation)
        { Exchanges++; Assert.NotNull(verifier); Assert.StartsWith("https://127.0.0.1:", redirect); return Task.FromResult(new DiscordGrant("synthetic-discord-grant", "42", 120, ["identify"])); }
        public Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation) => Task.FromResult("42");
        public Task Revoke(string accessToken, CancellationToken cancellation) { Revocations++; return Task.CompletedTask; }
    }
}
