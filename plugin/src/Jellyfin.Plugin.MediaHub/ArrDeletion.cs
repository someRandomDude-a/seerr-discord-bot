using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ArrFile(int Id, string Path, long Size, string Added, int Season = 0);
public sealed record ArrProof(ArrRequest Request, string Url, string KeyHash, int Instance, int LibraryId, string Path, IReadOnlyList<ArrFile> Files, string Added = "");
public sealed record PreparedArr(ArrProof Proof, string ApiKey, int ConfigurationRevision = -1);
public interface IArrDeletion
{
    Task<ArrProof> Capture(User owner, VerifiedDeletionTarget target, CancellationToken cancellation);
    Task<PreparedArr> Prepare(User owner, ArrProof proof, CancellationToken cancellation);
    Task Delete(PreparedArr prepared, CancellationToken cancellation);
    Task<bool> Gone(User owner, ArrProof proof, CancellationToken cancellation);
    Task Unmonitor(User owner, ArrProof proof, CancellationToken cancellation);
}

/// <summary>Arr owns all filesystem operations. This class only makes bounded, non-retrying HTTP calls.</summary>
public sealed class ArrDeletion : IArrDeletion, INativeDeletionResolver, IDisposable
{
    private readonly SettingsService settings;
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2);
    private readonly NativeDeletionReader? native;
    public ArrDeletion(SettingsService settings, NativeDeletionReader? native = null) : this(settings, ServiceAddress.Handler(), native) { }
    internal ArrDeletion(SettingsService settings, HttpMessageHandler handler, NativeDeletionReader? native = null)
    { this.settings = settings; this.native = native; http = new(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 }; }
    private void NativeAuthority(Guid owner, ArrRequest request)
    {
        var source = request.Native ?? throw new HubError("Native deletion authority is missing.");
        if (source.User != owner || source.Item == Guid.Empty || request.RequestId != 0 || request.MediaId != 0 || request.Instance is null) throw new HubError("Native proposal identity changed.");
        var current = (native ?? throw new HubError("Native deletion verification is unavailable.")).Read(owner, source.Item);
        if (current.Kind != request.Kind || current.Provider != request.Provider || current.Tmdb != request.Tmdb || MediaPath(current.Path) != MediaPath(request.NativePath)) throw new HubError("Native target identity or access changed.");
    }
    private async Task<JsonNode?> Send(HttpMethod method, string url, string key, JsonNode? body, CancellationToken cancellation)
    {
        ServiceAddress.Key(key);
        await slots.WaitAsync(cancellation);
        try
        {
        using var request = new HttpRequestMessage(method, url); request.Headers.Add("X-Api-Key", key);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellation);
        return string.IsNullOrWhiteSpace(text) ? null : SafeJson.Parse(text);
        }
        finally { slots.Release(); }
    }
    private async Task<JsonNode> Seerr(string path, CancellationToken cancellation)
    {
        var s = settings.Current;
        return await Send(HttpMethod.Get, s.SeerrUrl.TrimEnd('/') + "/api/v1/" + path, settings.Unprotect(s.SeerrKeyEncrypted), null, cancellation)
            ?? throw new HubError("Seerr verification unavailable.");
    }
    private static int Number(JsonNode row, string name) => row[name]?.GetValue<int>() ?? throw new HubError("Incomplete service identity.");
    private static string Text(JsonNode row, string name) => row[name]?.GetValue<string>() ?? throw new HubError("Incomplete service identity.");
    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
    private static string MediaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl)) throw new HubError("Invalid media path identity.");
        var normalized = Normalize(path); var drive = normalized.Length > 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/';
        if (!(drive || normalized.StartsWith('/')) || normalized.Length < (drive ? 4 : 2) || normalized.Contains("//", StringComparison.Ordinal) ||
            normalized[(drive ? 2 : 0)..].Contains(':') || normalized.Split('/').Any(p => p is "." or "..")) throw new HubError("Ambiguous media path identity.");
        return normalized;
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Resource(ArrRequest r) => r.Kind == "movie" ? "movie" : r.Kind == "tv" ? "series" : throw new HubError("Unsupported deletion target.");
    private static string Name(ArrRequest r) => r.Kind == "movie" ? "radarr" : "sonarr";
    private async Task<(string Url, string Key, int Id)> Destination(User owner, ArrRequest r, CancellationToken cancellation, bool afterRemoval = false)
    {
        Resource(r); var revision = settings.Current.Revision;
        if (r.Provider <= 0 || r.Tmdb <= 0 || r.Instance < 0) throw new HubError("Invalid pinned request identity.");
        if (settings.Current.SeerrUrl.TrimEnd('/') != r.Origin) throw new HubError("Seerr destination changed.");
        if (r.Native is not null) NativeAuthority(owner.Id, r);
        else
        {
        new DeletionPermissions(settings).Require(owner, native: false);
        if (r.RequestId <= 0 || r.MediaId <= 0) throw new HubError("Invalid pinned request identity.");
        var request = await Seerr("request/" + r.RequestId, cancellation);
        var user = await Seerr("user/" + Number(request["requestedBy"]!, "id"), cancellation);
        if (Number(request, "status") is not (2 or 5) || Number(request, "id") != r.RequestId || Number(user, "id") != Number(request["requestedBy"]!, "id") ||
            !Guid.TryParse(Text(user, "jellyfinUserId"), out var userId) || userId != owner.Id || Text(request, "type") != r.Kind ||
            request["is4k"]?.GetValue<bool>() != r.Is4k || Number(request["media"]!, "id") != r.MediaId ||
            Number(request["media"]!, "tmdbId") != r.Tmdb ||
            Number(request["media"]!, r.Kind == "movie" ? "tmdbId" : "tvdbId") != r.Provider)
            throw new HubError("Request ownership or target changed.");
        var instance = request["media"]![r.Is4k ? "serviceId4k" : "serviceId"]?.GetValue<int>();
        if (instance != r.Instance && !(afterRemoval && instance is null && r.Instance is not null)) throw new HubError("Arr destination changed.");
        }
        if (r.Kind == "tv")
        {
            var details = await Seerr("tv/" + r.Tmdb, cancellation);
            if (details["externalIds"]?["tvdbId"]?.GetValue<int>() != r.Provider) throw new HubError("TV identity changed.");
        }
        var rows = (await Seerr("settings/" + Name(r), cancellation)).AsArray();
        if (rows.Count > 100 || rows.Any(x => x is null) || rows.Select(x => Number(x!, "id")).Distinct().Count() != rows.Count) throw new HubError("Ambiguous Arr configuration.");
        var selected = rows.Where(x => r.Instance is int id ? Number(x!, "id") == id : x!["isDefault"]?.GetValue<bool>() == true && (x["is4k"]?.GetValue<bool>() ?? false) == r.Is4k).ToArray();
        if (selected.Length != 1 || (selected[0]!["is4k"]?.GetValue<bool>() ?? false) != r.Is4k) throw new HubError("No unique Arr destination for this edition.");
        var row = selected[0]!; var port = Number(row, "port");
        var host = Text(row, "hostname"); var basePath = row["baseUrl"]?.GetValue<string>() ?? "";
        if (port is < 1 or > 65535 || host.Contains('/') || host.Contains('@') || basePath.IndexOfAny(['?', '#']) >= 0) throw new HubError("Invalid Arr URL.");
        var url = ServiceAddress.Discovered(host, port, basePath, row["useSsl"]?.GetValue<bool>() == true);
        var key = Text(row, "apiKey"); if (key.Length == 0) throw new HubError("Arr key is missing.");
        if (revision != settings.Current.Revision) throw new HubError("Integration settings changed during destination verification.");
        settings.RequireArr(Name(r), Number(row, "id"), url);
        if (r.Native is not null) NativeAuthority(owner.Id, r);
        return (url, key, Number(row, "id"));
    }
    private async Task<JsonObject?> Entry(ArrRequest r, string url, string key, CancellationToken cancellation)
    {
        var provider = r.Kind == "movie" ? "tmdbId" : "tvdbId";
        var rows = (await Send(HttpMethod.Get, url + "/api/v3/" + Resource(r) + "?" + provider + "=" + r.Provider, key, null, cancellation))?.AsArray()
            ?? throw new HubError("Incomplete Arr response.");
        if (rows.Any(x => x is null || Number(x, "id") <= 0 || Number(x, provider) <= 0)) throw new HubError("Invalid Arr library response.");
        var matches = rows.Where(x => Number(x!, provider) == r.Provider).ToArray();
        if (matches.Length > 1) throw new HubError("Ambiguous Arr library target.");
        return matches.SingleOrDefault()?.AsObject();
    }
    private async Task<IReadOnlyList<ArrFile>> Files(ArrRequest r, JsonObject entry, string url, string key, CancellationToken cancellation)
    {
        var kind = r.Kind == "movie" ? "moviefile" : "episodefile"; var foreign = r.Kind == "movie" ? "movieId" : "seriesId";
        var rows = (await Send(HttpMethod.Get, url + "/api/v3/" + kind + "?" + foreign + "=" + Number(entry, "id"), key, null, cancellation))?.AsArray()
            ?? throw new HubError("Incomplete Arr file response.");
        if (rows.Count > 10000) throw new HubError("Arr file scan is too large.");
        var files = new List<ArrFile>();
        foreach (var row in rows)
        {
            if (row is null || Number(row, foreign) != Number(entry, "id") || Number(row, "id") <= 0) throw new HubError("Arr file target mismatch.");
            var path = row["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                var relative = Text(row, "relativePath").Replace('\\', '/');
                if (relative.StartsWith('/') || relative.Split('/').Any(part => part is ".." or "." || part.Contains(':'))) throw new HubError("Invalid Arr relative path.");
                path = Normalize(Text(entry, "path")) + "/" + relative;
            }
            path = MediaPath(path); _ = MediaPath(Text(entry, "path"));
            if (!path.StartsWith(Normalize(Text(entry, "path")) + "/", StringComparison.Ordinal) || path.Split('/').Any(part => part == ".."))
                throw new HubError("Arr file lies outside its pinned media folder.");
            var size = row["size"]?.GetValue<long>() ?? throw new HubError("Missing file identity.");
            var added = Text(row, "dateAdded"); var season = r.Kind == "tv" ? Number(row, "seasonNumber") : 0;
            if (size < 0 || season < 0 || !DateTimeOffset.TryParse(added, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new HubError("Invalid Arr file identity.");
            files.Add(new(Number(row, "id"), path, size, added, season));
        }
        if (files.Select(f => f.Id).Distinct().Count() != files.Count) throw new HubError("Duplicate Arr files.");
        return files.OrderBy(f => f.Id).ToArray();
    }
    public async Task<ArrProof> Capture(User owner, VerifiedDeletionTarget target, CancellationToken cancellation)
    {
        var r = target.Arr ?? throw new HubError("Recreate this proposal to verify its Arr target.");
        _ = MediaPath(r.NativePath);
        var d = await Destination(owner, r, cancellation); var entry = await Entry(r, d.Url, d.Key, cancellation) ?? throw new HubError("Arr entry is missing.");
        var files = await Files(r, entry, d.Url, d.Key, cancellation);
        if (files.Count == 0 || (r.Kind == "movie" ? !files.Any(f => f.Path == Normalize(r.NativePath)) : Normalize(Text(entry, "path")) != Normalize(r.NativePath)))
            throw new HubError("Jellyfin and Arr paths do not match. Check container mounts.");
        // Multiple unlinked movie editions are ambiguous: never delete another file alongside the selected edition.
        if (r.Kind == "movie" && files.Count != 1) throw new HubError("Ambiguous movie edition.");
        await Scope(r, entry, files, cancellation);
        var added = Text(entry, "added"); if (!DateTimeOffset.TryParse(added, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) throw new HubError("Invalid Arr entry identity.");
        return new(r, d.Url, Hash(d.Key), d.Id, Number(entry, "id"), MediaPath(Text(entry, "path")), files, added);
    }
    public async Task<VerifiedDeletionTarget> Discover(User owner, Guid item, CancellationToken cancellation)
    {
        var selected = (native ?? throw new HubError("Native deletion verification is unavailable.")).Read(owner.Id, item);
        var revision = settings.Current.Revision; var service = selected.Kind == "movie" ? "radarr" : "sonarr";
        var approved = settings.Current.Features.ArrDestinations.Where(d => d.Service == service).ToArray();
        if (approved.Length == 0) throw new HubError("No approved Arr destination is configured for this item.");
        var configured = (await Seerr("settings/" + service, cancellation)).AsArray();
        if (configured.Count > 100 || configured.Any(row => row is null) || configured.Select(row => Number(row!, "id")).Distinct().Count() != configured.Count) throw new HubError("Arr destination discovery is incomplete or ambiguous.");
        var matches = new List<ArrRequest>();
        foreach (var destination in approved)
        {
            cancellation.ThrowIfCancellationRequested();
            var instance = configured.SingleOrDefault(row => Number(row!, "id") == destination.Instance) ?? throw new HubError("An approved destination is no longer configured. Uniqueness cannot be verified.");
            var request = new ArrRequest(settings.Current.SeerrUrl.TrimEnd('/'), 0, 0, selected.Kind, instance["is4k"]?.GetValue<bool>() ?? false,
                destination.Instance, selected.Provider, selected.Tmdb, selected.Path, new(owner.Id, item));
            var target = await Destination(owner, request, cancellation); var entry = await Entry(request, target.Url, target.Key, cancellation);
            if (entry is null) continue;
            var files = await Files(request, entry, target.Url, target.Key, cancellation);
            if (files.Count == 0 || (selected.Kind == "movie" ? !files.Any(file => file.Path == MediaPath(selected.Path)) : MediaPath(Text(entry, "path")) != MediaPath(selected.Path))) continue;
            if (selected.Kind == "movie" && files.Count != 1) throw new HubError("The selected movie has ambiguous Arr file editions.");
            await Scope(request, entry, files, cancellation); matches.Add(request);
        }
        if (matches.Count != 1 || revision != settings.Current.Revision) throw new HubError("No unique, complete and currently approved Arr target could be verified.");
        var match = matches[0]; NativeAuthority(owner.Id, match);
        return new(item, "native-v1|" + match.Origin + "|" + match.Kind + "|" + match.Provider + "|" + match.Instance + "|" + Hash(MediaPath(match.NativePath)), match);
    }
    private async Task Scope(ArrRequest r, JsonObject entry, IReadOnlyList<ArrFile> files, CancellationToken cancellation)
    {
        if (r.Kind == "tv")
        {
            var requested = r.Native is null ? (await Seerr("request/" + r.RequestId, cancellation))["seasons"]?.AsArray() ?? throw new HubError("Request season scope unavailable.")
                : entry["seasons"]?.AsArray() ?? throw new HubError("Native series season scope unavailable.");
            var covered = requested.Select(season => Number(season!, "seasonNumber")).ToHashSet();
            var seasons = entry["seasons"]?.AsArray() ?? throw new HubError("Arr season scope unavailable.");
            if (requested.Count is 0 or > 1000 || covered.Count != requested.Count || covered.Any(n => n < 0) || seasons.Count > 1000 || seasons.Select(s => Number(s!, "seasonNumber")).Distinct().Count() != seasons.Count)
                throw new HubError("Invalid or ambiguous season scope.");
            if (files.Any(file => !covered.Contains(file.Season)) || seasons.Any(season => season is null ||
                season["monitored"]?.GetValue<bool>() != false && !covered.Contains(Number(season, "seasonNumber"))))
                throw new HubError("Request does not cover the whole show. Use Sonarr for partial-season removal.");
        }
    }
    private async Task<(string Key, JsonObject? Entry, IReadOnlyList<ArrFile> Files)> Current(User owner, ArrProof proof, CancellationToken cancellation, bool afterRemoval = false)
    {
        var d = await Destination(owner, proof.Request, cancellation, afterRemoval);
        if (d.Url != proof.Url || d.Id != proof.Instance || Hash(d.Key) != proof.KeyHash) throw new HubError("Pinned Arr destination changed.");
        var entry = await Entry(proof.Request, d.Url, d.Key, cancellation);
        if (entry is null) return (d.Key, null, []);
        if (proof.Added.Length == 0 || Number(entry, "id") != proof.LibraryId || Normalize(Text(entry, "path")) != proof.Path || Text(entry, "added") != proof.Added) throw new HubError("Arr entry was replaced or moved.");
        return (d.Key, entry, await Files(proof.Request, entry, d.Url, d.Key, cancellation));
    }
    public async Task<PreparedArr> Prepare(User owner, ArrProof proof, CancellationToken cancellation)
    {
        var revision = settings.Current.Revision;
        var current = await Current(owner, proof, cancellation);
        if (current.Entry is null || !current.Files.SequenceEqual(proof.Files)) throw new HubError("Arr files changed. Recreate the proposal.");
        await Scope(proof.Request, current.Entry, current.Files, cancellation);
        if (revision != settings.Current.Revision) throw new HubError("Integration settings changed during deletion preparation.");
        return new(proof, current.Key, revision);
    }
    public async Task Delete(PreparedArr prepared, CancellationToken cancellation)
    {
        var p = prepared.Proof; var movie = p.Request.Kind == "movie";
        if (p.Request.Native is { } source) NativeAuthority(source.User, p.Request);
        Resource(p.Request); settings.RequireArr(Name(p.Request), p.Instance, p.Url);
        var config = settings.Current;
        if (!config.AutomaticDeletion || prepared.ConfigurationRevision != config.Revision || Hash(prepared.ApiKey) != p.KeyHash) throw new HubError("Deletion authorization or configuration changed. No deletion was sent.");
        if (p.LibraryId <= 0 || p.Files is null || p.Files.Count is 0 or > 10000 || p.Files.Any(f => f is null || f.Id <= 0 || f.Size < 0) || p.Files.Select(f => f.Id).Distinct().Count() != p.Files.Count)
            throw new HubError("Invalid pinned Arr file selection. No deletion was sent.");
        var body = new JsonObject { [movie ? "movieFileIds" : "episodeFileIds"] = new JsonArray(p.Files.Select(f => JsonValue.Create(f.Id)).Cast<JsonNode?>().ToArray()) };
        await Send(HttpMethod.Delete, p.Url + "/api/v3/" + (movie ? "moviefile" : "episodefile") + "/bulk", prepared.ApiKey, body, cancellation);
    }
    public async Task<bool> Gone(User owner, ArrProof proof, CancellationToken cancellation)
    {
        var current = await Current(owner, proof, cancellation, true);
        if (current.Files.Any(file => !proof.Files.Contains(file))) throw new HubError("Replacement files detected. Manual review required.");
        return current.Files.Count == 0;
    }
    public async Task Unmonitor(User owner, ArrProof proof, CancellationToken cancellation)
    {
        var revision = settings.Current.Revision;
        var current = await Current(owner, proof, cancellation, true);
        if (current.Entry is null) return; // Never recreate absent entries.
        if (current.Files.Count != 0) throw new HubError("Files remain. Cleanup is blocked.");
        await Scope(proof.Request, current.Entry, current.Files, cancellation);
        var r = proof.Request; var url = proof.Url + "/api/v3/"; var entry = current.Entry;
        async Task Mutate(HttpMethod method, string path, JsonNode body)
        {
            settings.RequireArr(Name(r), proof.Instance, proof.Url);
            if (revision != settings.Current.Revision) throw new HubError("Integration settings changed during cleanup.");
            await Send(method, url + path, current.Key, body, cancellation);
        }
        async Task<JsonArray> Episodes()
        {
            var rows = (await Send(HttpMethod.Get, url + "episode?seriesId=" + proof.LibraryId, current.Key, null, cancellation))?.AsArray() ?? throw new HubError("Episode scan incomplete.");
            var scope = r.Native is null ? (await Seerr("request/" + r.RequestId, cancellation))["seasons"]?.AsArray() : entry["seasons"]?.AsArray();
            var covered = scope?.Select(s => Number(s!, "seasonNumber")).ToHashSet() ?? throw new HubError("Season scope unavailable.");
            if (rows.Count > 10000 || rows.Any(ep => ep is null || Number(ep, "seriesId") != proof.LibraryId || Number(ep, "id") <= 0 || Number(ep, "seasonNumber") < 0 || ep["monitored"] is null ||
                ep["monitored"]!.GetValue<bool>() && !covered.Contains(Number(ep, "seasonNumber"))) || rows.Select(ep => Number(ep!, "id")).Distinct().Count() != rows.Count) throw new HubError("Invalid, incomplete or unowned episode scope.");
            return rows;
        }
        if (r.Kind == "movie")
        {
            if (entry["monitored"]?.GetValue<bool>() != false)
                await Mutate(HttpMethod.Put, "movie/editor", new JsonObject { ["movieIds"] = new JsonArray(proof.LibraryId), ["monitored"] = false });
        }
        else
        {
            var seasons = entry["seasons"]?.AsArray() ?? throw new HubError("Season scan incomplete.");
            var episodes = await Episodes(); // Validate the entire scope before the first monitoring mutation.
            if (entry["monitored"]?.GetValue<bool>() != false || entry["monitorNewItems"]?.GetValue<string>() != "none")
            {
                // Never PUT a full, stale Series resource: that can overwrite paths/profiles/tags changed by another operator.
                await Mutate(HttpMethod.Put, "series/editor", new JsonObject { ["seriesIds"] = new JsonArray(proof.LibraryId), ["monitored"] = false, ["monitorNewItems"] = "none" });
            }
            if (seasons.Any(s => s?["monitored"]?.GetValue<bool>() != false) || episodes.Any(ep => ep!["monitored"]?.GetValue<bool>() != false))
            {
                // Recheck replacement files and identity immediately before this separate monitoring-only mutation.
                var check = await Current(owner, proof, cancellation, true);
                if (check.Entry is null) return;
                if (check.Files.Count != 0) throw new HubError("Replacement files detected. Cleanup is blocked.");
                await Scope(proof.Request, check.Entry, check.Files, cancellation);
                var latestEpisodes = await Episodes();
                if (!episodes.Select(ep => Number(ep!, "id")).ToHashSet().SetEquals(latestEpisodes.Select(ep => Number(ep!, "id")))) throw new HubError("Episode scope changed during cleanup.");
                await Mutate(HttpMethod.Post, "seasonpass", new JsonObject {
                    ["series"] = new JsonArray(new JsonObject { ["id"] = proof.LibraryId, ["monitored"] = false }),
                    ["monitoringOptions"] = new JsonObject { ["monitor"] = "none" } });
            }
            var verified = await Episodes();
            if (!episodes.Select(ep => Number(ep!, "id")).ToHashSet().SetEquals(verified.Select(ep => Number(ep!, "id"))) || verified.Any(ep => ep!["monitored"]?.GetValue<bool>() != false)) throw new HubError("Episode cleanup not confirmed.");
        }
        var fresh = await Current(owner, proof, cancellation, true);
        if (fresh.Entry is not null && (fresh.Files.Count != 0 || fresh.Entry["monitored"]?.GetValue<bool>() != false || r.Kind == "tv" &&
            (fresh.Entry["monitorNewItems"]?.GetValue<string>() != "none" || fresh.Entry["seasons"]!.AsArray().Any(s => s?["monitored"]?.GetValue<bool>() != false))))
            throw new HubError("Unmonitoring not confirmed.");
    }
    public void Dispose() => http.Dispose();
}
