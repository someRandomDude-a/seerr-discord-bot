using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

public interface IDiscordOAuth
{
    Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation);
    Task<string> Verify(string accessToken, string? guild, bool activity, CancellationToken cancellation);
    Task Revoke(string accessToken, CancellationToken cancellation) => Task.CompletedTask;
}
public sealed class DiscordOAuth : IDiscordOAuth, IDisposable
{
    private readonly Func<OAuthConfiguration> configuration;
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(4);
    public DiscordOAuth(Func<OAuthConfiguration> configuration) : this(configuration, ServiceAddress.Handler()) { }
    internal DiscordOAuth(Func<OAuthConfiguration> configuration, HttpMessageHandler handler) { this.configuration = configuration; http = new(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 }; }
    private async Task<JsonNode> Request(string path, string? bearer, Dictionary<string, string>? form, CancellationToken cancellation)
    {
        await slots.WaitAsync(cancellation);
        try
        {
            using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Post, "https://discord.com/api/v10/" + path);
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            using var response = await http.SendAsync(request, cancellation); response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellation); return text.Length == 0 ? new JsonObject() : SafeJson.Parse(text, 2 * 1024 * 1024);
        }
        finally { slots.Release(); }
    }
    public async Task<DiscordGrant> Exchange(string code, string? redirect, string? verifier, CancellationToken cancellation)
    {
        if (code.Length is < 1 or > 1024) throw new HubError("Invalid OAuth code."); var f = configuration();
        if (f.ApplicationId.Length == 0 || f.ClientSecret.Length == 0) throw new HubError("Discord OAuth is not configured. Use explicit code approval instead.");
        var form = new Dictionary<string, string> { ["client_id"] = f.ApplicationId, ["client_secret"] = f.ClientSecret, ["grant_type"] = "authorization_code", ["code"] = code };
        if (redirect is not null) form["redirect_uri"] = redirect; if (verifier is not null) form["code_verifier"] = verifier;
        var result = await Request("oauth2/token", null, form, cancellation); var token = result["access_token"]!.GetValue<string>();
        if (token.Length is < 1 or > 4096 || !string.Equals(result["token_type"]?.GetValue<string>(), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new HubError("Discord did not return a valid OAuth grant.");
        var id = await Verify(token, null, false, cancellation); var scopes = (result["scope"]?.GetValue<string>() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("identify")) throw new HubError("Discord identity scope was not granted.");
        return new(token, id, Math.Clamp(result["expires_in"]?.GetValue<int>() ?? 60, 1, 900), scopes);
    }
    public async Task<string> Verify(string token, string? guild, bool activity, CancellationToken cancellation)
    {
        var s = configuration(); var authorization = await Request("oauth2/@me", token, null, cancellation);
        var scopes = authorization["scopes"]!.AsArray().Select(v => v!.GetValue<string>()).ToHashSet();
        if (authorization["application"]?["id"]?.GetValue<string>() != s.ApplicationId || !scopes.Contains("identify")) throw new HubError("Discord application or identity scope changed.");
        var user = await Request("users/@me", token, null, cancellation); var id = user["id"]!.GetValue<string>();
        if (!ulong.TryParse(id, out var numeric) || numeric == 0 || user["bot"]?.GetValue<bool>() == true) throw new HubError("A verified human Discord identity is required.");
        if (activity)
        {
            if (!s.ActivityEnabled || guild is null || !s.AllowedGuilds.Contains(guild) || !scopes.Contains("guilds")) throw new HubError("Open Activity in an allowed Discord server with the required scopes.");
            string? after = null; var found = false;
            for (var page = 0; page < 50; page++)
            {
                var rows = (await Request("users/@me/guilds?limit=200" + (after is null ? "" : "&after=" + after), token, null, cancellation)).AsArray();
                if (rows.Any(row => row?["id"]?.GetValue<string>() == guild)) { found = true; break; }
                if (rows.Count < 200) break;
                var next = rows.Last()!["id"]!.GetValue<string>(); if (!ulong.TryParse(next, out var n) || after is not null && n <= ulong.Parse(after)) throw new HubError("Discord membership scan incomplete."); after = next;
            }
            if (!found) throw new HubError("Your current server membership could not be verified.");
        }
        return id;
    }
    public async Task Revoke(string token, CancellationToken cancellation)
    {
        var f = configuration();
        _ = await Request("oauth2/token/revoke", null, new() { ["client_id"] = f.ApplicationId, ["client_secret"] = f.ClientSecret, ["token"] = token, ["token_type_hint"] = "access_token" }, cancellation);
    }
    public void Dispose() { http.Dispose(); slots.Dispose(); }
}
