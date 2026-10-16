using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class ActivitySessionTests
{
    private static ActivitySessions Sessions(FeatureFixture f, FakeOAuth oauth, TimeProvider? clock = null)
    { f.Update(f.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "private-oauth-secret", ActivityEnabled = true }); return new(f.Settings, f.Store, f.Identity, oauth, clock); }
    private static async Task<ActivityAuthentication> Authenticate(ActivitySessions sessions) => await sessions.Authenticate(sessions.Challenge("peer"), "code", "555", "peer", CancellationToken.None);
    [Fact]
    public async Task ActivityRequiresLiveDiscordMembershipAndNativeIdentityAndReturnsNoNativeCredential()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var sessions = Sessions(f, oauth); var auth = await Authenticate(sessions);
        var profile = await sessions.Profile(auth.Session, CancellationToken.None); Assert.True(profile.Linked); Assert.Equal(f.User.Id, profile.UserId);
        var json = JsonSerializer.Serialize(profile); Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("private-oauth-secret", json);
        oauth.Member = false; var denied = await Assert.ThrowsAsync<HubError>(() => sessions.Require(auth.Session, CancellationToken.None)); Assert.Equal("activity_denied", denied.Code);
        oauth.Member = true; f.User.SetPermission(PermissionKind.IsDisabled, true); await Assert.ThrowsAsync<HubError>(() => sessions.Require(auth.Session, CancellationToken.None)); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ChallengesArePeerBoundOneUseAndSessionsExpireOrLogoutWithoutRevokingJellyfin()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var clock = new Clock(); var sessions = Sessions(f, oauth, clock); var challenge = sessions.Challenge("peer");
        await Assert.ThrowsAsync<HubError>(() => sessions.Authenticate(challenge, "code", "555", "different-peer", CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => sessions.Authenticate(challenge, "code", "555", "peer", CancellationToken.None)); Assert.Equal(0, oauth.Exchanges);
        var auth = await Authenticate(sessions); var credential = f.Store.Credential("42"); sessions.Logout(auth.Session);
        await Assert.ThrowsAsync<HubError>(() => sessions.Profile(auth.Session, CancellationToken.None)); Assert.Equal(credential, f.Store.Credential("42"));
        auth = await Authenticate(sessions); clock.Advance(121); await Assert.ThrowsAsync<HubError>(() => sessions.Require(auth.Session, CancellationToken.None));
    }
    [Fact]
    public async Task UnlinkedDiscordSeesOnlyLinkStateAndCannotUseLibraryServices()
    {
        using var f = new FeatureFixture(); var sessions = Sessions(f, new FakeOAuth()); using (var db = f.Store.Open()) using (var remove = HubStore.Command(db, "DELETE FROM links")) remove.ExecuteNonQuery();
        var auth = await Authenticate(sessions); var profile = await sessions.Profile(auth.Session, CancellationToken.None); Assert.False(profile.Linked); Assert.Null(profile.UserId); Assert.Null(profile.Name);
        await Assert.ThrowsAsync<HubError>(() => sessions.Require(auth.Session, CancellationToken.None)); f.Library.VerifyNoOtherCalls(); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task LogoutAndConfigurationChangesRejectAnAlreadyInFlightPrivateRead()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var sessions = Sessions(f, oauth); var auth = await Authenticate(sessions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oauth.BeforeVerify = async () => { entered.SetResult(); await release.Task; };
        var read = sessions.Require(auth.Session, CancellationToken.None); await entered.Task;
        sessions.Logout(auth.Session); release.SetResult(); await Assert.ThrowsAsync<HubError>(() => read);
        oauth.BeforeVerify = null; auth = await Authenticate(sessions);
        f.Update(f.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "different-secret", ActivityEnabled = true });
        await Assert.ThrowsAsync<HubError>(() => sessions.Require(auth.Session, CancellationToken.None)); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ActivityRouteIgnoresForgedUserIdsAndCannotUseNativeAdministratorConfiguration()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var sessions = Sessions(f, oauth); var auth = await Authenticate(sessions);
        var collection = new ServiceCollection(); new ServiceRegistrator().RegisterServices(collection, Mock.Of<IServerApplicationHost>());
        collection.AddSingleton(f.Store); collection.AddSingleton(f.Settings); collection.AddSingleton(f.Identity); collection.AddSingleton(f.Bridge); collection.AddSingleton(sessions);
        collection.AddSingleton(f.Users.Object); collection.AddSingleton(f.Library.Object); collection.AddSingleton(f.Data.Object); collection.AddSingleton(f.Sessions.Object); collection.AddSingleton(Mock.Of<ISyncPlayManager>());
        using var services = collection.BuildServiceProvider();
        ActivityController Controller(string method, string? body = null)
        {
            var http = new DefaultHttpContext(); http.Request.Method = method; http.Request.Headers["X-MediaHub-Session"] = auth.Session;
            if (body is not null) { http.Request.ContentType = "application/json"; http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body)); }
            return new(services) { ControllerContext = new() { HttpContext = http } };
        }
        var response = Assert.IsType<OkObjectResult>(await Controller("POST", "{\"Revision\":0,\"UserId\":\"someone-else\",\"Preferences\":{\"Muted\":true}}").Api("Me", CancellationToken.None));
        Assert.True(Assert.IsType<PersonalState>(response.Value).Preferences.Muted);
        using (var db = f.Store.Open()) using (var who = HubStore.Command(db, "SELECT user FROM personal")) Assert.Equal(f.User.Id.ToString("N"), who.ExecuteScalar());
        f.User.SetPermission(PermissionKind.IsAdministrator, true);
        var denied = Assert.IsAssignableFrom<ObjectResult>(await Controller("GET").Api("Settings", CancellationToken.None)); Assert.Equal(400, denied.StatusCode);
        var noSession = Controller("GET"); noSession.Request.Headers.Remove("X-MediaHub-Session"); noSession.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-IsApiKey", "True")], "ApiKey"));
        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(await noSession.Api("Me", CancellationToken.None)).StatusCode);
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task DiscordOAuthPinsApplicationAudienceScopesAndDoesNotPutSecretsInUrls()
    {
        using var f = new FeatureFixture(); _ = Sessions(f, new FakeOAuth()); var handler = new OAuthHandler(); using var oauth = new DiscordOAuth(f.Settings, handler);
        var grant = await oauth.Exchange("one-use-code", "https://jellyfin.example/base/MediaHub/Link/OAuth/Callback", "pkce-verifier", CancellationToken.None); Assert.Equal("42", grant.Discord);
        Assert.Equal("42", await oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
        handler.WrongApp = true; await Assert.ThrowsAsync<HubError>(() => oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
        handler.WrongApp = false; handler.Member = false; await Assert.ThrowsAsync<HubError>(() => oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
    }
    [Fact]
    public async Task ActivityDirectoryRootIsEmbeddableButCannotServeArbitraryPluginFiles()
    {
        using var f = new FeatureFixture(); _ = Sessions(f, new FakeOAuth());
        using var services = new ServiceCollection().AddSingleton(f.Settings).BuildServiceProvider();
        var http = new DefaultHttpContext(); http.Request.Path = "/MediaHub/Activity/"; http.Request.PathBase = "/base";
        var controller = new ActivityController(services) { ControllerContext = new() { HttpContext = http } };
        var page = Assert.IsType<ContentResult>(await controller.App()); Assert.Contains("Assets/activity.bundle.js", page.Content);
        Assert.DoesNotContain("private-oauth-secret", page.Content); Assert.Contains("frame-ancestors https://discord.com", http.Response.Headers.ContentSecurityPolicy.ToString());
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await controller.Asset("settings.json")).StatusCode);
        http.Request.Headers.Origin = "https://someone-else.discordsays.com";
        Assert.Equal(401, Assert.IsAssignableFrom<ObjectResult>(await controller.Config()).StatusCode);
        http.Request.Headers.Remove("Origin"); http.Request.Path = "/MediaHub/Activity";
        Assert.Equal("/base/MediaHub/Activity/", Assert.IsType<RedirectResult>(await controller.App()).Url);
    }
    [Fact]
    public void ActivityPaletteRejectsArbitraryCssWithoutChangingSavedPreferences()
    {
        using var f = new FeatureFixture(); var personal = new PersonalService(f.Store, Mock.Of<IViewerMedia>());
        Assert.Throws<HubError>(() => personal.Save(f.User, 0, new(Theme: new("url(http://private)", "#eeeeee", "#00a4dc", "#101010")))); Assert.Equal(0, personal.State(f.User).Revision);
        Assert.Equal("#112233", personal.Save(f.User, 0, new(Theme: new("#112233", "#eeeeee", "#00a4dc", "#101010"))).Preferences.Theme!.Background);
    }
    private sealed class Clock : TimeProvider { private DateTimeOffset now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => now; public void Advance(int seconds) => now = now.AddSeconds(seconds); }
    private sealed class FakeOAuth : IDiscordOAuth
    {
        public bool Member = true; public int Exchanges; public Func<Task>? BeforeVerify;
        public Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation) { Exchanges++; return Task.FromResult(new DiscordGrant("discord-private-access", "42", 120, ["identify", "guilds"])); }
        public async Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation) { if (BeforeVerify is not null) await BeforeVerify(); if (!Member || guild != "555" || !activity) throw new HubError("Membership denied"); return "42"; }
    }
    private sealed class OAuthHandler : HttpMessageHandler
    {
        public bool WrongApp, Member = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("discord.com", request.RequestUri!.Host); Assert.DoesNotContain("private-oauth-secret", request.RequestUri.ToString()); Assert.False(request.Headers.Contains("X-Emby-Token")); object body;
            if (request.RequestUri.AbsolutePath.EndsWith("/oauth2/token"))
            { var form = await request.Content!.ReadAsStringAsync(cancellationToken); Assert.Contains("client_secret=private-oauth-secret", form); Assert.Contains("code_verifier=pkce-verifier", form); body = new { access_token = "discord-access", token_type = "Bearer", expires_in = 900, scope = "identify guilds" }; }
            else if (request.RequestUri.AbsolutePath.EndsWith("/oauth2/@me")) body = new { application = new { id = WrongApp ? "999" : "123456" }, scopes = new[] { "identify", "guilds" } };
            else if (request.RequestUri.AbsolutePath.EndsWith("/users/@me")) body = new { id = "42", bot = false };
            else body = Member ? new[] { new { id = "555" } } : Array.Empty<object>();
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
