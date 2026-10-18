using System.Text.Json;

namespace Jellyfin.Plugin.MediaHub;

public sealed record DiscordGrant(string AccessToken, string Discord, int ExpiresIn, string[] Scopes);
public interface IDiscordOAuth
{
    Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation);
    Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation);
    Task Revoke(string accessToken, CancellationToken cancellation) => Task.CompletedTask;
}

/// <summary>Retained native link/session authority stays here; all Discord network traffic is optional transport.</summary>
public sealed class OptionalDiscordOAuth(CoreApiProvider api) : IDiscordOAuth
{
    public async Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation) =>
        JsonSerializer.Deserialize<DiscordGrant>(await api.Transport("oauth.exchange", new { Code = code, Redirect = redirect, Verifier = verifier }, cancellation))!;
    public async Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation) =>
        JsonSerializer.Deserialize<string>(await api.Transport("oauth.verify", new { AccessToken = accessToken, Guild = guild, Activity = activity }, cancellation))!;
    public async Task Revoke(string accessToken, CancellationToken cancellation) =>
        _ = await api.Transport("oauth.revoke", new { AccessToken = accessToken }, cancellation);
}
