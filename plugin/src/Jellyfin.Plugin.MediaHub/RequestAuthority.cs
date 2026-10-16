using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ArrRequest(string Origin, int RequestId, int MediaId, string Kind, bool Is4k, int? Instance,
    int Provider, int Tmdb, string NativePath);
public sealed record VerifiedDeletionTarget(Guid Item, string Pin, ArrRequest? Arr = null);
public interface IRequestAuthority
{
    Task<VerifiedDeletionTarget> Verify(User actor, int requestId, Guid item, CancellationToken cancellation);
    Task<IReadOnlyList<OwnedRequest>> FindOwned(User actor, Guid item, CancellationToken cancellation) => throw new HubError("Request discovery unavailable.");
}
public sealed record OwnedRequest(int RequestId, bool Is4k);

/// <summary>Live ownership + exact media/edition checks. Never forwards upstream JSON to viewers.</summary>
public sealed class RequestAuthority : IRequestAuthority, IDisposable
{
    private readonly SettingsService settings;
    private readonly ILibraryManager library;
    private readonly HttpClient http;
    public RequestAuthority(SettingsService settings, ILibraryManager library) : this(settings, library, ServiceAddress.Handler()) { }
    internal RequestAuthority(SettingsService settings, ILibraryManager library, HttpMessageHandler handler)
    {
        this.settings = settings; this.library = library;
        http = new(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
    }
    public async Task<IReadOnlyList<OwnedRequest>> FindOwned(User actor, Guid itemId, CancellationToken cancellation)
    {
        var item = library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(actor)) throw new HubError("Item unavailable.");
        item = MediaActivity.Root(item);
        if (!item.ProviderIds.TryGetValue("Tmdb", out var tmdb)) return [];
        var config = settings.Current; var result = new List<OwnedRequest>();
        for (var skip = 0; skip < 1000; skip += 100)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.SeerrUrl.TrimEnd('/') + "/api/v1/request?take=100&skip=" + skip);
            request.Headers.Add("X-Api-Key", settings.Unprotect(config.SeerrKeyEncrypted));
            using var response = await http.SendAsync(request, cancellation); response.EnsureSuccessStatusCode();
            var page = SafeJson.Read<JsonElement>(await response.Content.ReadAsStringAsync(cancellation), 1024 * 1024);
            var rows = page.GetProperty("results").EnumerateArray().ToArray();
            if (rows.Length > 100) throw new HubError("Request scan exceeded its page budget.");
            foreach (var row in rows.Where(row => row.GetProperty("media").GetProperty("tmdbId").ToString() == tmdb && row.GetProperty("status").GetInt32() is 2 or 5))
            {
                try { var verified = await Verify(actor, row.GetProperty("id").GetInt32(), item.Id, cancellation); result.Add(new(row.GetProperty("id").GetInt32(), verified.Arr!.Is4k)); }
                catch (HubError) { /* Never disclose another requester's details. */ }
            }
            if (rows.Length < 100) return result;
        }
        throw new HubError("Request scan incomplete. Use your request ID in Media Hub.");
    }
    public async Task<VerifiedDeletionTarget> Verify(User actor, int requestId, Guid itemId, CancellationToken cancellation)
    {
        if (requestId <= 0) throw new HubError("Invalid request.");
        var config = settings.Current;
        if (config.SeerrUrl.Length == 0 || config.SeerrKeyEncrypted.Length == 0) throw new HubError("Seerr is not configured.");
        var item = library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(actor) || MediaActivity.Root(item).Id != itemId) throw new HubError("Item unavailable.");
        async Task<JsonElement> Read(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.SeerrUrl.TrimEnd('/') + "/api/v1/" + path);
            request.Headers.Add("X-Api-Key", settings.Unprotect(config.SeerrKeyEncrypted));
            using var response = await http.SendAsync(request, cancellation); response.EnsureSuccessStatusCode();
            return SafeJson.Read<JsonElement>(await response.Content.ReadAsStringAsync(cancellation), 1024 * 1024);
        }
        var row = await Read("request/" + requestId);
        if (row.GetProperty("id").GetInt32() != requestId) throw new HubError("Request changed. Refresh.");
        if (row.GetProperty("status").GetInt32() is not (2 or 5)) throw new HubError("Only approved requests can propose deletion.");
        var owner = await Read("user/" + row.GetProperty("requestedBy").GetProperty("id").GetInt32());
        if (owner.GetProperty("id").GetInt32() != row.GetProperty("requestedBy").GetProperty("id").GetInt32()) throw new HubError("Request owner changed.");
        if (!Guid.TryParse(owner.GetProperty("jellyfinUserId").GetString(), out var user) || user != actor.Id)
            throw new HubError("You can only delete your own requests.");
        var media = row.GetProperty("media");
        var kind = row.GetProperty("type").GetString();
        var expected = item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : "";
        if (kind != expected || !item.ProviderIds.TryGetValue("Tmdb", out var tmdb) || tmdb != media.GetProperty("tmdbId").GetInt32().ToString())
            throw new HubError("Request and library item do not match.");
        // Ownership is verified above; duplicate targets are shared across requesters.
        var is4k = row.GetProperty("is4k").GetBoolean();
        var instance = media.TryGetProperty(is4k ? "serviceId4k" : "serviceId", out var selected) && selected.ValueKind == JsonValueKind.Number ? selected.GetInt32() : (int?)null;
        var provider = kind == "movie" ? media.GetProperty("tmdbId").GetInt32() : media.GetProperty("tvdbId").GetInt32();
        if (provider <= 0 || media.GetProperty("id").GetInt32() <= 0 || instance < 0) throw new HubError("Invalid request/provider identity.");
        return new(item.Id, string.Join('|', config.SeerrUrl.TrimEnd('/'), media.GetProperty("id").GetInt32(),
            row.GetProperty("is4k").GetBoolean(), media.TryGetProperty("serviceId", out var service) ? service.ToString() : "",
            media.TryGetProperty("serviceId4k", out var fourK) ? fourK.ToString() : ""),
            new(config.SeerrUrl.TrimEnd('/'), requestId, media.GetProperty("id").GetInt32(), kind!, is4k, instance, provider, media.GetProperty("tmdbId").GetInt32(), item.Path ?? ""));
    }
    public void Dispose() => http.Dispose();
}
