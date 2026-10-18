using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record SeerrViewer(int Id, long Permissions, string Origin);

/// <summary>Native identity mapping followed by isolated X-API-User requests. Never substitutes Seerr administrator permissions.</summary>
public sealed class SeerrBridge(HubStore store, SettingsService settings, HostIdentity identity, Func<HttpMessageHandler>? handlers = null)
{
    private readonly SemaphoreSlim slots = new(6);
    public async Task<SeerrScope> Open(User actor, CancellationToken cancellation)
    {
        identity.Active(actor.Id); var config = settings.Current;
        if (config.SeerrUrl.Length == 0 || config.SeerrKeyEncrypted.Length == 0) throw new HubError("Seerr is not configured.");
        var origin = config.SeerrUrl.TrimEnd('/'); var key = settings.Unprotect(config.SeerrKeyEncrypted);
        var admin = new SeerrScope(settings, identity, actor.Id, origin, key, null, slots, handlers?.Invoke());
        var matches = new HashSet<int>(); var seen = new HashSet<int>();
        try
        {
            int? total = null;
            for (var skip = 0; ; skip += 100)
            {
                if (skip >= 10000) throw new HubError("Seerr account scan incomplete.");
                var page = await admin.Get("user?take=100&skip=" + skip, cancellation); var rows = page["results"]!.AsArray();
                if (rows.Count > 100) throw new HubError("Seerr account page exceeds its budget.");
                if (page["pageInfo"]?["results"] is JsonNode count)
                { var current = count.GetValue<int>(); if (current is < 0 or > 10000 || total is not null && total != current) throw new HubError("Seerr accounts changed or exceeded the scan budget. Try again."); total = current; }
                foreach (var row in rows)
                {
                    var id = row!["id"]!.GetValue<int>(); if (id <= 0 || !seen.Add(id)) throw new HubError("Seerr account scan changed.");
                    if (Guid.TryParse(row["jellyfinUserId"]?.GetValue<string>(), out var mapped) && mapped == actor.Id) matches.Add(id);
                }
                if (rows.Count < 100) { if (total is not null && total != seen.Count) throw new HubError("Seerr account scan incomplete."); break; }
            }
        }
        finally { admin.Dispose(); }
        if (matches.Count != 1) throw new HubError("Import your Jellyfin account into Seerr and resolve duplicate mappings.");
        var scope = new SeerrScope(settings, identity, actor.Id, origin, key, matches.Single(), slots, handlers?.Invoke());
        try
        {
            await scope.Verify(cancellation);
            using var db = store.Open(); using var tx = db.BeginTransaction();
            settings.Protection.Check(db, tx, "watches");
            using var add = HubStore.Command(db, "INSERT OR IGNORE INTO seerr_bindings VALUES($0,$1,$2)", actor.Id.ToString("N"), origin, scope.Viewer.Id);
            add.Transaction = tx; add.ExecuteNonQuery();
            using var read = HubStore.Command(db, "SELECT origin,seerr_id FROM seerr_bindings WHERE user=$0", actor.Id.ToString("N")); read.Transaction = tx;
            using (var r = read.ExecuteReader()) if (!r.Read() || r.GetString(0) != origin || r.GetInt32(1) != scope.Viewer.Id) throw new HubError("Seerr mapping changed. Administrator review is required; no changes were sent.");
            settings.Protection.Seal(db, tx, "watches"); tx.Commit(); return scope;
        }
        catch { scope.Dispose(); throw; }
    }
}

public sealed class SeerrScope : IDisposable
{
    private readonly SettingsService settings;
    private readonly HostIdentity identity;
    private readonly Guid user;
    private readonly string origin, key;
    private readonly int? id;
    private readonly HttpClient http;
    private readonly CookieContainer cookies = new();
    private readonly SemaphoreSlim slots;
    private bool csrf;
    public SeerrViewer Viewer { get; private set; }
    internal SeerrScope(SettingsService settings, HostIdentity identity, Guid user, string origin, string key, int? id, SemaphoreSlim slots, HttpMessageHandler? handler = null)
    {
        this.settings = settings; this.identity = identity; this.user = user; this.origin = ServiceAddress.Url(origin); ServiceAddress.Key(key); this.key = key; this.id = id; this.slots = slots;
        var transport = ServiceAddress.Handler(); transport.UseCookies = true; transport.CookieContainer = cookies;
        http = new(handler ?? transport) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        if (handler is not null) transport.Dispose();
        Viewer = new(id ?? 0, 0, origin);
    }
    public bool Can(long permissions) => (Viewer.Permissions & (2 | permissions)) != 0;
    public async Task Verify(CancellationToken cancellation)
    {
        identity.Active(user); var current = settings.Current;
        if (current.SeerrUrl.TrimEnd('/') != origin || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(settings.Unprotect(current.SeerrKeyEncrypted)), Encoding.UTF8.GetBytes(key)))
            throw new HubError("Seerr configuration changed. Reopen the page.");
        if (id is not int uid) throw new HubError("A user-scoped Seerr session is required.");
        var me = await Get("auth/me", cancellation);
        var mapped = await Get("user/" + uid, cancellation);
        if (me["id"]?.GetValue<int>() != uid || mapped["id"]?.GetValue<int>() != uid ||
            !Guid.TryParse(mapped["jellyfinUserId"]?.GetValue<string>(), out var actual) || actual != user)
            throw new HubError("Seerr did not verify your account context.");
        var permissions = me["permissions"]?.GetValue<long>() ?? throw new HubError("Seerr permission verification is incomplete.");
        if (permissions < 0) throw new HubError("Invalid Seerr permission flags.");
        Viewer = new(uid, permissions, origin);
    }
    public Task<JsonNode> Get(string path, CancellationToken cancellation) => Send(HttpMethod.Get, path, null, cancellation);
    public async Task<JsonNode> Write(HttpMethod method, string path, JsonNode? body, CancellationToken cancellation)
    {
        if (id is null || method == HttpMethod.Get) throw new HubError("Invalid scoped write.");
        await Verify(cancellation);
        if (!csrf) { await Get("settings/public", cancellation); csrf = true; }
        return await Send(method, path, body, cancellation);
    }
    private async Task<JsonNode> Send(HttpMethod method, string path, JsonNode? body, CancellationToken cancellation)
    {
        ServiceAddress.Route(path);
        await slots.WaitAsync(cancellation);
        try
        {
            using var request = new HttpRequestMessage(method, origin + "/api/v1/" + path);
            request.Headers.Add("X-Api-Key", key); request.Headers.Add("Origin", new Uri(origin).GetLeftPart(UriPartial.Authority));
            if (id is int uid) request.Headers.Add("X-API-User", uid.ToString());
            var cookie = cookies.GetCookies(new Uri(origin))["XSRF-TOKEN"];
            if (cookie is not null) request.Headers.Add("X-XSRF-Token", Uri.UnescapeDataString(cookie.Value));
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, cancellation);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values)
                try { cookies.SetCookies(new Uri(origin), value); } catch (CookieException) { /* Seerr will reject a write without a valid token. */ }
            if (!response.IsSuccessStatusCode) throw new HubError(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "Seerr denied this action." : "Seerr did not confirm the action.");
            var text = await response.Content.ReadAsStringAsync(cancellation);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : SafeJson.Parse(text, 2 * 1024 * 1024);
        }
        finally { slots.Release(); }
    }
    public void Dispose() => http.Dispose();
}
