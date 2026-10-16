using System.Security.Claims;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaHub.Tests;

public sealed class LinkControllerTests
{
    [Fact]
    public async Task CallbackCookieIsSecureHttpOnlyLaxAndScopedToTheConfiguredBasePath()
    {
        using var f = new FeatureFixture();
        f.Update(f.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "oauth-secret" });
        var flow = new LinkFlow(f.Store, f.Settings, f.Identity, f.Bridge, new NeverOAuth());
        using var services = new ServiceCollection().AddSingleton(flow).AddSingleton(f.Settings).BuildServiceProvider();
        var context = new DefaultHttpContext(); context.Request.Scheme = "https"; context.Request.Host = new("jellyfin.example"); context.Request.PathBase = "/base";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-UserId", f.User.Id.ToString()), new("Jellyfin-IsApiKey", "False"), new("Jellyfin-Token", "token"), new("Jellyfin-DeviceId", "device")], "Jellyfin"));
        var controller = new LinkController(services) { ControllerContext = new() { HttpContext = context } };
        var code = f.Store.BeginLink("42"); var before = f.Store.Credential("42");
        var landing = Assert.IsType<ContentResult>(controller.Landing(code));
        Assert.Contains("sessionStorage", landing.Content); Assert.DoesNotContain("oauth-secret", landing.Content);
        using (var db = f.Store.Open()) using (var count = HubStore.Command(db, "SELECT COUNT(*) FROM link_oauth")) Assert.Equal(0L, count.ExecuteScalar());
        var result = Assert.IsType<OkObjectResult>(await controller.Begin(new(code), CancellationToken.None)); Assert.IsType<LinkStart>(result.Value);
        var cookie = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=lax", cookie); Assert.Contains("path=/base/MediaHub/Link/OAuth/Callback", cookie);
        Assert.Equal(before, f.Store.Credential("42")); // Neither landing nor Begin can approve.
        context.Request.Scheme = "http"; Assert.IsType<BadRequestObjectResult>(await controller.Begin(new(code), CancellationToken.None));
        context.Request.Scheme = "https"; context.Request.Host = new("forwarded.example"); Assert.IsType<BadRequestObjectResult>(await controller.Begin(new(code), CancellationToken.None));
        var errorPage = Assert.IsType<ContentResult>(await controller.Callback(new string('a', 48), "code-from-another-browser", CancellationToken.None));
        Assert.DoesNotContain("code-from-another-browser", errorPage.Content); Assert.DoesNotContain("oauth-secret", errorPage.Content);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Contains("frame-ancestors 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
    }
    private sealed class NeverOAuth : IDiscordOAuth
    {
        public Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation) => throw new InvalidOperationException("A crawler or cookie-less callback must not exchange a code.");
        public Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation) => throw new InvalidOperationException();
    }
}
