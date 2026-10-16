using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class LinkFlowTests
{
    private static ClaimsPrincipal Principal(FeatureFixture f, string token = "token", string device = "device") => new(new ClaimsIdentity([
        new("Jellyfin-UserId", f.User.Id.ToString()), new("Jellyfin-DeviceId", device), new("Jellyfin-IsApiKey", "False"), new("Jellyfin-Token", token)], "Jellyfin"));
    private static LinkFlow Flow(FeatureFixture f, FakeOAuth oauth)
    { f.Update(f.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "client-private-secret" }); return new(f.Store, f.Settings, f.Identity, f.Bridge, oauth); }
    [Fact]
    public async Task BothIdentitiesAndAnAuthenticatedPostAreRequiredAndProofIsSingleUse()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var flow = Flow(f, oauth); var principal = Principal(f); var code = f.Store.BeginLink("42");
        var initial = f.Store.Credential("42"); var start = await flow.Begin(principal, code, CancellationToken.None);
        Assert.Contains("code_challenge_method=S256", start.AuthorizeUrl); Assert.Contains("redirect_uri=", start.AuthorizeUrl);
        var json = JsonSerializer.Serialize(start); Assert.DoesNotContain(start.Cookie, json); Assert.DoesNotContain("client-private-secret", json); Assert.DoesNotContain("token", start.AuthorizeUrl);
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(principal, code, CancellationToken.None)); Assert.Equal(initial, f.Store.Credential("42"));
        Assert.Equal(code, await flow.Callback(start.State, "authorization-code", start.Cookie, CancellationToken.None));
        Assert.Equal("proved", (await flow.Status(principal, code)).State); Assert.Equal(initial, f.Store.Credential("42")); // Callback GET proves Discord, not native linking.
        await flow.Complete(principal, code, CancellationToken.None); Assert.Equal(f.User.Id, f.Store.LinkedUser("42"));
        await flow.Complete(principal, code, CancellationToken.None); Assert.Equal(1, oauth.Exchanges); Assert.Equal(1, oauth.Revocations);
        await Assert.ThrowsAsync<HubError>(() => flow.Callback(start.State, "authorization-code", start.Cookie, CancellationToken.None)); Assert.Equal(1, oauth.Exchanges);
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ForwardedAuthorizeUrlWithoutSameBrowserCookieCannotProduceLinkProof()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var flow = Flow(f, oauth); var code = f.Store.BeginLink("42"); var start = await flow.Begin(Principal(f), code, CancellationToken.None);
        await Assert.ThrowsAsync<HubError>(() => flow.Callback(start.State, "victim-code", new string('a', 48), CancellationToken.None)); Assert.Equal(0, oauth.Exchanges);
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(Principal(f), code, CancellationToken.None));
    }
    [Fact]
    public async Task DifferentDiscordBrowserAndNativeDeviceCannotClaimTheHandoff()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth { Discord = "99" }; var flow = Flow(f, oauth); var code = f.Store.BeginLink("42"); var start = await flow.Begin(Principal(f), code, CancellationToken.None);
        await Assert.ThrowsAsync<HubError>(() => flow.Callback(start.State, "code", start.Cookie, CancellationToken.None)); Assert.Equal("failed", (await flow.Status(Principal(f), code)).State);
        oauth.Discord = "42"; start = await flow.Begin(Principal(f), code, CancellationToken.None); await flow.Callback(start.State, "code", start.Cookie, CancellationToken.None);
        var session = new SessionInfo(f.Sessions.Object, NullLogger.Instance) { Id = "other-browser", UserId = f.User.Id, DeviceId = "other-device" };
        f.Sessions.Setup(s => s.GetSessionByAuthenticationToken("other-token", "other-device", It.IsAny<string>())).ReturnsAsync(session);
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(Principal(f, "other-token", "other-device"), code, CancellationToken.None));
    }
    [Fact]
    public async Task ConfigChangeAndExpiredProofFailClosedWithoutReplacingExistingBinding()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var flow = Flow(f, oauth); var code = f.Store.BeginLink("42"); var before = f.Store.Credential("42"); var start = await flow.Begin(Principal(f), code, CancellationToken.None);
        await flow.Callback(start.State, "code", start.Cookie, CancellationToken.None);
        f.Update(f.Features() with { DiscordApplicationId = "123456", DiscordClientSecret = "changed-client-secret" });
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(Principal(f), code, CancellationToken.None)); Assert.Equal(before, f.Store.Credential("42"));
        using (var db = f.Store.Open()) using (var expire = HubStore.Command(db, "UPDATE link_oauth SET expires=$0", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))) expire.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(Principal(f), code, CancellationToken.None));
    }
    [Theory]
    [InlineData("discord")] [InlineData("expires")] [InlineData("status")]
    public async Task ChangedRetainedOAuthProofCannotLinkAnAccount(string field)
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var flow = Flow(f, oauth); var principal = Principal(f); var code = f.Store.BeginLink("42"); var before = f.Store.Credential("42");
        var start = await flow.Begin(principal, code, CancellationToken.None);
        if (field != "status") await flow.Callback(start.State, "code", start.Cookie, CancellationToken.None);
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, field switch {
            "discord" => "UPDATE link_oauth SET discord='99'",
            "expires" => "UPDATE link_oauth SET expires='2099-01-01T00:00:00.0000000+00:00'",
            _ => "UPDATE link_oauth SET status='proved',discord='42'" })) change.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => flow.Complete(principal, code, CancellationToken.None)); Assert.Equal(before, f.Store.Credential("42"));
    }
    [Fact]
    public async Task AlteredCallbackCookieBindingCannotReachTheOAuthProvider()
    {
        using var f = new FeatureFixture(); var oauth = new FakeOAuth(); var flow = Flow(f, oauth); var code = f.Store.BeginLink("42"); var start = await flow.Begin(Principal(f), code, CancellationToken.None);
        var replacement = new string('b', 48);
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, "UPDATE link_oauth SET cookie=$0", LinkFlow.Hash(replacement))) change.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => flow.Callback(start.State, "code", replacement, CancellationToken.None)); Assert.Equal(0, oauth.Exchanges);
    }
    private sealed class FakeOAuth : IDiscordOAuth
    {
        public string Discord = "42"; public int Exchanges, Revocations;
        public Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation)
        { Exchanges++; Assert.Equal("https://jellyfin.example/base/MediaHub/Link/OAuth/Callback", redirect); Assert.NotNull(verifier); return Task.FromResult(new DiscordGrant("discord-private-access", Discord, 120, ["identify"])); }
        public Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation) => Task.FromResult(Discord);
        public Task Revoke(string token, CancellationToken cancellation) { Revocations++; return Task.CompletedTask; }
    }
}
