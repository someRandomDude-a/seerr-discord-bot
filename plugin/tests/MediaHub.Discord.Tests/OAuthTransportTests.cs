extern alias Integration;
using System.Net;
using System.Net.Http.Json;
using Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;
using Xunit;
using DiscordOAuth = Integration::Jellyfin.Plugin.MediaHub.DiscordOAuth;
using HubError = Integration::Jellyfin.Plugin.MediaHub.HubError;

namespace MediaHub.Discord.Tests;

public sealed class OAuthTransportTests
{
    [Fact]
    public async Task DiscordOAuthPinsApplicationAudienceScopesAndDoesNotPutSecretsInUrls()
    {
        var handler = new OAuthHandler(); using var oauth = new DiscordOAuth(() => new OAuthConfiguration("123456", "private-oauth-secret", true, ["555"]), handler);
        var grant = await oauth.Exchange("one-use-code", "https://jellyfin.example/base/MediaHub/Link/OAuth/Callback", "pkce-verifier", CancellationToken.None); Assert.Equal("42", grant.Discord);
        Assert.Equal("42", await oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
        handler.WrongApp = true; await Assert.ThrowsAsync<HubError>(() => oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
        handler.WrongApp = false; handler.Member = false; await Assert.ThrowsAsync<HubError>(() => oauth.Verify(grant.AccessToken, "555", true, CancellationToken.None));
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
