using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ExtraCard(string Kind, string ForeignId, string Title, string Overview, string Status, bool Requestable, Guid? NativeId, IReadOnlyList<string> Editions, ActivityReceipt? Activity = null);
public sealed record ExtraPage(IReadOnlyList<ExtraCard> Items, int Page, int Pages);
public sealed record ExtraWatch(string Kind, string ForeignId, string Title, bool Subscribed, Guid? NativeId);

/// <summary>Selected album/book additions only. No PUT/DELETE, parent-wide search, exclusion removal or existing-book edition mutation.</summary>
public sealed class ExtraMedia(HubStore store, SettingsService settings, SeerrBridge bridge, ServiceHttp http, ILibraryManager library, MediaActivity activity)
{
    private static string U(User user) => user.Id.ToString("N");
    private static string RequestPurpose(string token, User user, string kind, string foreign, string parent, string? edition, string expires) =>
        "MediaHub.ExtraRequest.v1|" + JsonSerializer.Serialize(new { token, User = U(user), kind, foreign, parent, edition, expires });
    internal static void Kind(string kind) { if (kind is not ("music" or "book")) throw new HubError("Choose music or book."); }
    private static void Id(string kind, string id)
    { Kind(kind); if (kind == "music" ? !Guid.TryParseExact(id, "D", out _) : !int.TryParse(id, out var n) || n <= 0) throw new HubError("Invalid media identity."); }
    private ExtraServiceSettings Config(string kind) { Kind(kind); var s = settings.Current.Features; var c = kind == "music" ? s.Lidarr : s.Readarr; if (c.Url.Length == 0) throw new HubError("This media service is not configured."); return c; }
    private static string Resource(string kind) => kind == "music" ? "album" : "book";
    private static string Foreign(string kind) => kind == "music" ? "foreignAlbumId" : "foreignBookId";
    private static string Parent(string kind) => kind == "music" ? "artist" : "author";
    private static string ParentId(string kind) => kind == "music" ? "foreignArtistId" : "foreignAuthorId";
    private static string Text(JsonNode row, string field) => row[field]?.GetValue<string>() ?? throw new HubError("Incomplete media identity.");
    private string Pin(ExtraServiceSettings c) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c))));
    private Task<JsonNode> Get(ExtraServiceSettings c, string path, CancellationToken token) => http.Json(HttpMethod.Get, c.Url, "api/v1/" + path, settings.Unprotect(c.KeyEncrypted), null, token);
    private (bool Allowed, Guid? Native) Access(User user, string kind, string id)
    {
        var provider = kind == "music" ? "MusicBrainzReleaseGroup" : "GoodreadsWork"; // Goodreads edition IDs are not work IDs.
        var rows = library.GetItemList(new InternalItemsQuery { Recursive = true, Limit = 101, GroupByPresentationUniqueKey = false,
            IncludeItemTypes = kind == "music" ? [BaseItemKind.MusicAlbum] : [BaseItemKind.Book, BaseItemKind.AudioBook], HasAnyProviderId = new Dictionary<string, string> { [provider] = id } });
        if (rows.Count >= 101) throw new HubError("Media identity scan incomplete.");
        var matches = rows.Where(row => row.ProviderIds.TryGetValue(provider, out var actual) && actual == id).ToArray(); var visible = matches.Where(row => row.IsVisibleStandalone(user)).ToArray();
        return (matches.Length == 0 || visible.Length > 0, visible.Length == 1 ? visible[0].Id : null);
    }
    private ExtraCard Project(User user, string kind, JsonNode row)
    {
        var id = Text(row, Foreign(kind)); Id(kind, id); var access = Access(user, kind, id); if (!access.Allowed) throw new HubError("Item unavailable.");
        var stats = row["statistics"]; var present = (stats?[kind == "music" ? "trackFileCount" : "bookFileCount"]?.GetValue<int>() ?? 0) > 0 || (stats?["sizeOnDisk"]?.GetValue<long>() ?? 0) > 0 || row["bookFile"] is JsonObject;
        var count = stats?["trackFileCount"]?.GetValue<int>() ?? 0; var total = stats?["totalTrackCount"]?.GetValue<int>() ?? count;
        var available = present && access.Native is not null;
        return new(kind, id, row["title"]?.GetValue<string>() ?? "Untitled", row["overview"]?.GetValue<string>() ?? "", available ? count > 0 && count < total ? "Partially available" : "Available" : (row["id"]?.GetValue<int>() ?? 0) > 0 ? "Requested" : "Not in your library",
            (row["id"]?.GetValue<int>() ?? 0) == 0, access.Native, kind == "book" ? Editions(row["editions"]) : []);
    }
    private static IReadOnlyList<string> Editions(JsonNode? value)
    {
        if (value is null) return []; var rows = value.AsArray(); if (rows.Count > 1000) throw new HubError("Too many book edition choices.");
        var editions = rows.Select(e => Text(e!, "foreignEditionId")).ToArray();
        if (editions.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(char.IsControl)) || editions.Distinct(StringComparer.Ordinal).Count() != editions.Length) throw new HubError("Ambiguous book edition identity.");
        return editions;
    }
    private async Task<JsonNode> Lookup(ExtraServiceSettings c, string kind, string id, CancellationToken cancellation)
    {
        Id(kind, id); var rows = (await Get(c, Resource(kind) + "/lookup?term=" + Uri.EscapeDataString((kind == "music" ? "lidarr:" : "work:") + id), cancellation)).AsArray();
        var matches = rows.Where(r => r is not null && r[Foreign(kind)]?.GetValue<string>() == id).ToArray();
        if (rows.Count > 500 || matches.Length != 1) throw new HubError("No unique media match. Search again."); return matches[0]!;
    }
    public async Task<ExtraPage> Search(User user, string kind, string query, int page, CancellationToken cancellation)
    {
        if (query.Length is < 1 or > 100 || page is < 1 or > 25) throw new HubError("Invalid search.");
        using var scope = await bridge.Open(user, cancellation); var c = Config(kind);
        var rows = (await Get(c, Resource(kind) + "/lookup?term=" + Uri.EscapeDataString(query), cancellation)).AsArray(); if (rows.Count > 500) throw new HubError("Search returned too many results.");
        var cards = new List<ExtraCard>(); var ids = new HashSet<string>();
        foreach (var row in rows)
        {
            var id = Text(row!, Foreign(kind)); Id(kind, id); if (!ids.Add(id)) throw new HubError("Ambiguous search identities.");
            if (Access(user, kind, id).Allowed) cards.Add(Project(user, kind, row!));
        }
        await scope.Verify(cancellation); return new(cards.Skip((page - 1) * 20).Take(20).ToArray(), page, Math.Max(1, (cards.Count + 19) / 20));
    }
    public async Task<ExtraCard> Details(User user, string kind, string id, bool interact, CancellationToken cancellation)
    {
        using var scope = await bridge.Open(user, cancellation); var row = await Lookup(Config(kind), kind, id, cancellation); var card = Project(user, kind, row);
        await scope.Verify(cancellation);
        return interact && card.NativeId is Guid native ? card with { Activity = activity.Record(user, native, MediaInteraction.Details) } : card;
    }
    private async Task Ready(ExtraServiceSettings c, string kind, string id, string parent, CancellationToken cancellation)
    {
        var roots = (await Get(c, "rootfolder", cancellation)).AsArray(); var quality = (await Get(c, "qualityprofile", cancellation)).AsArray(); var metadata = (await Get(c, "metadataprofile", cancellation)).AsArray();
        if (roots.Count(r => r?["path"]?.GetValue<string>() == c.Root && r["accessible"]?.GetValue<bool>() != false) != 1 || quality.Count(r => r?["id"]?.GetValue<int>() == c.Quality) != 1 || metadata.Count(r => r?["id"]?.GetValue<int>() == c.Metadata) != 1) throw new HubError("Request root or profiles could not be verified.");
        var exclusions = (await Get(c, "importlistexclusion", cancellation)).AsArray();
        if (exclusions.Count > 10000 || exclusions.Any(e => e?["foreignId"] is null || Text(e, "foreignId") == id || Text(e, "foreignId") == parent)) throw new HubError("The service excludes this item/parent. No exclusion was changed.");
        var items = (await Get(c, Resource(kind), cancellation)).AsArray();
        if (items.Count > 10000 || items.Any(r => r is null || r[Foreign(kind)] is null)) throw new HubError("Library scan incomplete.");
        if (items.Any(r => Text(r!, Foreign(kind)) == id)) throw new HubError("Already in the service. No existing album/book or edition was modified.");
    }
    public async Task<RequestPlan> Prepare(User user, string kind, string id, string? edition, CancellationToken cancellation)
    {
        using var scope = await bridge.Open(user, cancellation); if (!scope.Can(32 | 262144 | 524288)) throw new HubError("Request permission is required.");
        var c = Config(kind); if (!c.Requests) throw new HubError("Requests are disabled for this service.");
        var row = await Lookup(c, kind, id, cancellation); var card = Project(user, kind, row); var parent = Text(row[Parent(kind)]!, ParentId(kind));
        if (!card.Requestable) throw new HubError("Already in the service. No existing edition was modified.");
        if (kind == "book")
        {
            if (edition is null && card.Editions.Count == 1) edition = card.Editions[0];
            if (edition is null || !card.Editions.Contains(edition, StringComparer.Ordinal)) throw new HubError("Select one verified book edition.");
        }
        await Ready(c, kind, id, parent, cancellation); await scope.Verify(cancellation);
        var token = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        using var db = store.Open(); using var tx = db.BeginTransaction();
        settings.Protection.Check(db, tx, "extras");
        using var expire = HubStore.Command(db, "UPDATE extra_requests SET state='expired' WHERE user=$0 AND state='prepared' AND expires<=$1", U(user), DateTimeOffset.UtcNow.ToString("O")); expire.Transaction = tx; expire.ExecuteNonQuery();
        using var pending = HubStore.Command(db, "SELECT COUNT(*) FROM extra_requests WHERE user=$0 AND state IN ('prepared','sending','uncertain')", U(user)); pending.Transaction = tx;
        if ((long)pending.ExecuteScalar()! >= 20) throw new HubError("Too many pending or uncertain requests. Review them first.");
        using var add = HubStore.Command(db, "INSERT INTO extra_requests VALUES($0,$1,$2,$3,$4,$5,$6,$7,'prepared',NULL,$8,$9)", token, U(user), kind, id, card.Title,
            settings.Protect(JsonSerializer.Serialize(new { Configuration = Pin(c), scope.Viewer.Origin, scope.Viewer.Id }), RequestPurpose(token, user, kind, id, parent, edition, expires.ToString("O"))), parent, edition, expires.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));
        add.Transaction = tx; add.ExecuteNonQuery(); settings.Protection.Seal(db, tx, "extras"); tx.Commit(); return new(token, card.Title, expires);
    }
    public RequestReceipt Receipt(User user, string token)
    {
        settings.Protection.Check("extras");
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT state,result FROM extra_requests WHERE id=$0 AND user=$1", token, U(user)); using var r = read.ExecuteReader();
        if (!r.Read()) throw new HubError("Request plan unavailable."); return new(token, r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1));
    }
    public IReadOnlyList<string> OwnNotificationRequests(User user)
    {
        settings.Protection.Check("extras");
        var result = new List<string>();
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT id FROM extra_requests WHERE user=$0 AND state IN ('completed','sending','uncertain') ORDER BY created DESC,id LIMIT 201", U(user));
        using var r = read.ExecuteReader(); while (r.Read()) result.Add(r.GetString(0));
        // Bound work to the most recent 200 local requests; old request history itself is not deleted.
        return result.Take(200).ToArray();
    }
    public async Task<ExtraCard> OwnRequestStatus(User user, string token, CancellationToken cancellation)
    {
        settings.Protection.Check("extras");
        string kind, id, parent, expires, protectedPin; string? edition;
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT kind,foreign_id,parent,edition,pin,expires FROM extra_requests WHERE id=$0 AND user=$1 AND state IN ('completed','sending','uncertain')", token, U(user)))
        using (var r = read.ExecuteReader()) { if (!r.Read()) throw new HubError("Own music/book request unavailable."); kind = r.GetString(0); id = r.GetString(1); parent = r.GetString(2); edition = r.IsDBNull(3) ? null : r.GetString(3); protectedPin = r.GetString(4); expires = r.GetString(5); }
        var proof = settings.Unprotect(protectedPin, RequestPurpose(token, user, kind, id, parent, edition, expires));
        using var scope = await bridge.Open(user, cancellation); var c = Config(kind);
        if (proof != JsonSerializer.Serialize(new { Configuration = Pin(c), scope.Viewer.Origin, scope.Viewer.Id })) throw new HubError("Request service or identity changed.");
        var row = await Lookup(c, kind, id, cancellation);
        if (Text(row[Parent(kind)]!, ParentId(kind)) != parent || edition is not null && !Project(user, kind, row).Editions.Contains(edition)) throw new HubError("Request parent or edition changed.");
        await scope.Verify(cancellation); return Project(user, kind, row); // No activity or changes, including for an uncertain send.
    }
    public RequestReceipt Cancel(User user, string token)
    {
        var changed = settings.Protection.Update("extras", (db, tx) => { using var cancel = HubStore.Command(db, "UPDATE extra_requests SET state='cancelled' WHERE id=$0 AND user=$1 AND state='prepared'", token, U(user)); cancel.Transaction = tx; return cancel.ExecuteNonQuery(); });
        if (changed != 1) throw new HubError("Only an unsent plan can be cancelled."); return Receipt(user, token);
    }
    public async Task<RequestReceipt> Confirm(User user, string token, Func<Task> verifyActor, CancellationToken cancellation)
    {
        settings.Protection.Check("extras");
        if (settings.Protection.LegacyRequest(token)) throw new HubError("Legacy request authorization is read-only. Inspect the service; cancel an unsent preview explicitly and prepare a new selection. No retry was sent.");
        string kind, id, parent, pin; string? edition;
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT kind,foreign_id,parent,edition,pin,state,expires FROM extra_requests WHERE id=$0 AND user=$1", token, U(user))) using (var r = read.ExecuteReader())
        {
            if (!r.Read()) throw new HubError("Request plan unavailable."); if (r.GetString(5) != "prepared") return Receipt(user, token);
            if (DateTimeOffset.Parse(r.GetString(6)) <= DateTimeOffset.UtcNow) throw new HubError("Request plan expired.");
            kind = r.GetString(0); id = r.GetString(1); parent = r.GetString(2); edition = r.IsDBNull(3) ? null : r.GetString(3); pin = settings.Unprotect(r.GetString(4), RequestPurpose(token, user, kind, id, parent, edition, r.GetString(6)));
        }
        using var scope = await bridge.Open(user, cancellation); var c = Config(kind);
        if (!c.Requests || !scope.Can(32 | 262144 | 524288) || pin != JsonSerializer.Serialize(new { Configuration = Pin(c), scope.Viewer.Origin, scope.Viewer.Id })) throw new HubError("Request destination or permission changed.");
        var row = await Lookup(c, kind, id, cancellation); var card = Project(user, kind, row);
        if (Text(row[Parent(kind)]!, ParentId(kind)) != parent || edition is not null && !card.Editions.Contains(edition)) throw new HubError("Media parent or edition changed.");
        await Ready(c, kind, id, parent, cancellation); await verifyActor(); await scope.Verify(cancellation);
        if (Pin(Config(kind)) != Pin(c)) throw new HubError("Service settings changed.");
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            settings.Protection.Check(db, tx, "extras");
            using var used = HubStore.Command(db, "SELECT COUNT(*) FROM extra_requests WHERE user=$0 AND kind=$1 AND state IN ('sending','uncertain','completed') AND created>=$2", U(user), kind, DateTimeOffset.UtcNow.AddDays(-1).ToString("O")); used.Transaction = tx;
            if ((long)used.ExecuteScalar()! >= c.DailyLimit) throw new HubError("Daily request limit reached.");
            using var claim = HubStore.Command(db, "UPDATE extra_requests SET state='sending',created=$0 WHERE id=$1 AND user=$2 AND state='prepared' AND expires>$0", DateTimeOffset.UtcNow.ToString("O"), token, U(user)); claim.Transaction = tx;
            if (claim.ExecuteNonQuery() != 1) { tx.Rollback(); return Receipt(user, token); } settings.Protection.Seal(db, tx, "extras"); tx.Commit();
        }
        var parentBody = new JsonObject { [ParentId(kind)] = parent, [kind == "music" ? "artistName" : "authorName"] = row[Parent(kind)]?[kind == "music" ? "artistName" : "authorName"]?.GetValue<string>() ?? "Unknown",
            ["rootFolderPath"] = c.Root, ["qualityProfileId"] = c.Quality, ["metadataProfileId"] = c.Metadata, ["monitored"] = true, ["monitorNewItems"] = "none",
            ["addOptions"] = new JsonObject { ["monitor"] = "none", [kind == "music" ? "searchForMissingAlbums" : "searchForMissingBooks"] = false } };
        var body = new JsonObject { [Foreign(kind)] = id, ["title"] = card.Title, [Parent(kind)] = parentBody, ["monitored"] = true,
            ["addOptions"] = new JsonObject { [kind == "music" ? "searchForNewAlbum" : "searchForNewBook"] = true } };
        if (edition is not null) body["editions"] = new JsonArray(new JsonObject { ["foreignEditionId"] = edition, ["monitored"] = true });
        var state = "uncertain"; int? resultId = null;
        try
        {
            var result = await http.Json(HttpMethod.Post, c.Url, "api/v1/" + Resource(kind), settings.Unprotect(c.KeyEncrypted), body, cancellation);
            if (result[Foreign(kind)]?.GetValue<string>() != id || (result["id"]?.GetValue<int>() ?? 0) <= 0) throw new HubError("Request identity was not confirmed.");
            resultId = result["id"]!.GetValue<int>(); state = "completed";
        }
        catch { /* The claim remains terminal until an explicitly reviewed recovery; never resend uncertain additions. */ }
        settings.Protection.Update("extras", (db, tx) => { using var save = HubStore.Command(db, "UPDATE extra_requests SET state=$0,result=$1 WHERE id=$2 AND state='sending'", state, resultId, token); save.Transaction = tx; return save.ExecuteNonQuery(); });
        ActivityReceipt? recorded = null;
        if (state == "completed" && card.NativeId is Guid native) try { recorded = activity.Record(user, native, MediaInteraction.Request); } catch { recorded = new(false, false); }
        return Receipt(user, token) with { Activity = recorded };
    }
    public IReadOnlyList<ExtraWatch> Watches(User user)
    {
        settings.Protection.Check("watches");
        var result = new List<ExtraWatch>(); var rows = new List<(string Kind, string Id, string Title, bool Subscribed)>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT kind,foreign_id,title,subscribed FROM extra_watches WHERE user=$0 ORDER BY kind,foreign_id LIMIT 201", U(user))) using (var r = read.ExecuteReader()) while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0));
        if (rows.Count > 200) throw new HubError("Retained watchlist exceeds its safe size.");
        foreach (var row in rows) { var access = Access(user, row.Kind, row.Id); if (access.Allowed) result.Add(new(row.Kind, row.Id, row.Title, row.Subscribed, access.Native)); } return result;
    }
    public async Task<ActivityReceipt?> Follow(User user, string kind, string id, bool? subscribed, long revision, Func<Task> verifyActor, CancellationToken cancellation, bool saveOnly = false)
    {
        var card = await Details(user, kind, id, false, cancellation); await verifyActor();
        using var db = store.Open(); using var tx = db.BeginTransaction(); settings.Protection.Check(db, tx, "watches"); WatchlistSync.Touch(db, tx, user);
        using var expected = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", U(user)); expected.Transaction = tx;
        if ((long)expected.ExecuteScalar()! != revision + 1) throw new HubError("Watchlist changed. Refresh first.");
        using var count = HubStore.Command(db, "SELECT (SELECT COUNT(*) FROM extra_watches WHERE user=$0 AND NOT(kind=$1 AND foreign_id=$2))+(SELECT COUNT(*) FROM external_watches WHERE user=$0)+(SELECT COUNT(*) FROM subscriptions WHERE user=$0 AND item NOT IN (SELECT native FROM external_watches WHERE user=$0 AND native IS NOT NULL))", U(user), kind, id); count.Transaction = tx;
        if ((subscribed == true || saveOnly) && (long)count.ExecuteScalar()! >= 200) throw new HubError("Watchlist is full.");
        using var write = HubStore.Command(db, saveOnly ? "INSERT INTO extra_watches VALUES($0,$1,$2,$3,0) ON CONFLICT(user,kind,foreign_id) DO NOTHING" : subscribed is null ? "DELETE FROM extra_watches WHERE user=$0 AND kind=$1 AND foreign_id=$2" : subscribed == false ? "UPDATE extra_watches SET subscribed=0 WHERE user=$0 AND kind=$1 AND foreign_id=$2" :
            "INSERT INTO extra_watches VALUES($0,$1,$2,$3,1) ON CONFLICT(user,kind,foreign_id) DO UPDATE SET title=excluded.title,subscribed=1", U(user), kind, id, card.Title); write.Transaction = tx; write.ExecuteNonQuery();
        if (subscribed != true && !saveOnly) WatchlistSync.ClearAlerts(db, tx, user, kind + ":" + id);
        settings.Protection.Seal(db, tx, "watches"); tx.Commit(); return (subscribed == true || saveOnly) && card.NativeId is Guid native ? activity.Record(user, native, MediaInteraction.Follow) : null;
    }
}
