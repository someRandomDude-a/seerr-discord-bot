using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record MediaKey(string Kind, int Id);
public sealed record ExternalFollow(string Kind, int ExternalId, string Title, bool Subscribed, Guid? NativeId, IReadOnlyList<Guid>? NativeIds = null);
public sealed record WatchSyncResult(bool Complete, int Pending);
public sealed record WatchSyncIssue(string Kind, int ExternalId, string Title, string State, bool Desired, long Revision);
public sealed record WatchSyncReview(string Token, string Title, bool LocalMembership, bool RemoteMembership, DateTimeOffset Expires);
public sealed record LegacyWatchReview(string Token, IReadOnlyList<string> Titles, DateTimeOffset Expires);
public interface IExternalMedia
{
    (MediaKey Key, string Title)? Identify(User user, Guid native);
    (bool Disclosable, Guid? Native) Resolve(User user, MediaKey key);
}
public sealed class ExternalMedia(ILibraryManager library) : IExternalMedia
{
    public (MediaKey Key, string Title)? Identify(User user, Guid native)
    {
        var item = library.GetItemById(native); if (item is null || !item.IsVisibleStandalone(user)) return null;
        item = MediaActivity.Root(item);
        var kind = item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : null;
        if (kind is null || !item.ProviderIds.TryGetValue("Tmdb", out var provider) || !int.TryParse(provider, out var id) || id <= 0) return null;
        return (new(kind, id), item.Name);
    }
    public (bool Disclosable, Guid? Native) Resolve(User user, MediaKey key)
    {
        MediaCatalog.Kind(key.Kind);
        var rows = library.GetItemList(new InternalItemsQuery { Recursive = true, Limit = 101, GroupByPresentationUniqueKey = false, GroupBySeriesPresentationUniqueKey = false,
            IncludeItemTypes = [key.Kind == "movie" ? Jellyfin.Data.Enums.BaseItemKind.Movie : Jellyfin.Data.Enums.BaseItemKind.Series],
            HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = key.Id.ToString() } });
        if (rows.Count >= 101) throw new HubError("Edition scan incomplete.");
        var matched = rows.Where(item => item.ProviderIds.TryGetValue("Tmdb", out var id) && id == key.Id.ToString()).ToArray();
        var visible = matched.Where(item => item.IsVisibleStandalone(user)).ToArray();
        return (matched.Length == 0 || visible.Length > 0, visible.Length == 1 ? visible[0].Id : null);
    }
}

/// <summary>Only movie/TV watchlist membership. No requests, monitoring changes, favorites or media deletions.</summary>
public sealed class WatchlistSync(HubStore store, SeerrBridge bridge, IExternalMedia media, MediaActivity? activity = null, SettingsService? settings = null, IViewerMedia? nativeMedia = null)
{
    private SettingsService Configuration => settings ?? new SettingsService(store);
    private StateIntegrity Protection => Configuration.Protection;
    private static string U(User user) => user.Id.ToString("N");
    private sealed record Recovery(Guid User, string Origin, int Seerr, int Configuration, MediaKey Key, long PersonalRevision, long IntentRevision, string State, bool Desired, bool Remote, DateTimeOffset Expires);
    private const string RecoveryPurpose = "MediaHub.WatchlistRecovery.v1";
    private sealed record NativeWatch(Guid Id, bool Subscribed, (MediaKey Key, string Title)? Identity);
    private (long Revision, IReadOnlyList<NativeWatch> Rows) NativeWatches(User user)
    {
        long revision; var rows = new List<(Guid Id, bool Subscribed)>();
        using (var db = store.Open()) using (var tx = db.BeginTransaction(deferred: true))
        {
            Protection.Check(db, tx, "watches");
            using var current = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", U(user)); current.Transaction = tx;
            revision = (long?)current.ExecuteScalar() ?? 0;
            using var read = HubStore.Command(db, "SELECT item,subscribed FROM subscriptions WHERE user=$0 ORDER BY item LIMIT 201", U(user)); read.Transaction = tx;
            using (var r = read.ExecuteReader()) while (r.Read())
            {
                if (!Guid.TryParseExact(r.GetString(0), "N", out var id) || r.GetInt64(1) is not (0 or 1)) throw new HubError("Invalid retained native watch. Nothing was reset.");
                rows.Add((id, r.GetInt64(1) == 1));
            }
            tx.Commit();
        }
        if (revision < 0 || rows.Count > 200) throw new HubError("Retained watchlist exceeds its safe budget.");
        // Native library reads happen outside SQLite transactions; only currently visible identities qualify.
        return (revision, rows.Select(r => new NativeWatch(r.Id, r.Subscribed, media.Identify(user, r.Id))).ToArray());
    }
    internal IReadOnlyList<Guid> NativeAliases(User user, MediaKey key) => NativeWatches(user).Rows.Where(r => r.Identity?.Key == key).Select(r => r.Id).ToArray();
    private static void CheckRevision(SqliteConnection db, SqliteTransaction tx, User user, long revision)
    {
        using var current = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", U(user)); current.Transaction = tx;
        if (((long?)current.ExecuteScalar() ?? 0) != revision) throw new HubError("Your watchlist changed during the scan. Refresh and review before syncing.");
    }
    private static void Mirror(SqliteConnection db, SqliteTransaction tx, User user, IEnumerable<Guid> aliases, bool? subscribed)
    {
        foreach (var id in aliases.Distinct())
        {
            using var write = HubStore.Command(db, subscribed is null ? "DELETE FROM subscriptions WHERE user=$0 AND item=$1" :
                "UPDATE subscriptions SET subscribed=$2 WHERE user=$0 AND item=$1", U(user), id.ToString("N"), subscribed == true ? 1 : 0);
            write.Transaction = tx; write.ExecuteNonQuery();
            if (subscribed != true) ClearAlerts(db, tx, user, id.ToString("N"));
        }
    }
    internal static void Touch(SqliteConnection db, SqliteTransaction tx, User user)
    {
        using var ensure = HubStore.Command(db, "INSERT OR IGNORE INTO personal VALUES($0,0,$1)", U(user), JsonSerializer.Serialize(new PersonalPreferences())); ensure.Transaction = tx; ensure.ExecuteNonQuery();
        using var bump = HubStore.Command(db, "UPDATE personal SET revision=revision+1 WHERE user=$0", U(user)); bump.Transaction = tx; bump.ExecuteNonQuery();
    }
    internal void NativeIntent(SqliteConnection db, SqliteTransaction tx, User user, Guid native, (MediaKey Key, string Title)? identity, bool? subscribed, IReadOnlyList<Guid>? aliases = null, bool saveOnly = false)
    {
        if (identity is not { } mapped) return;
        Set(db, tx, user, mapped.Key, mapped.Title, native, subscribed, saveOnly);
        if (!saveOnly) Mirror(db, tx, user, aliases ?? [], subscribed);
    }
    internal (MediaKey Key, string Title)? Identify(User user, Guid native) => media.Identify(user, native);
    internal void RemoveNative(SqliteConnection db, SqliteTransaction tx, User user, Guid native, (MediaKey Key, string Title)? identity, IReadOnlyList<Guid> aliases)
    {
        using var read = HubStore.Command(db, "SELECT kind,external_id,title FROM external_watches WHERE user=$0 AND native=$1", U(user), native.ToString("N")); read.Transaction = tx;
        var rows = new List<(MediaKey Key, string Title)>();
        using (var r = read.ExecuteReader()) while (r.Read()) rows.Add((new(r.GetString(0), r.GetInt32(1)), r.GetString(2)));
        if (identity is { } mapped && rows.All(row => row.Key != mapped.Key)) rows.Add(mapped);
        foreach (var row in rows) Set(db, tx, user, row.Key, row.Title, native, null);
        Mirror(db, tx, user, aliases, null);
    }
    private static void Set(SqliteConnection db, SqliteTransaction tx, User user, MediaKey key, string title, Guid? native, bool? subscribed, bool saveOnly = false)
    {
        if (subscribed is null)
        {
            using var remove = HubStore.Command(db, "DELETE FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); remove.Transaction = tx; remove.ExecuteNonQuery();
        }
        else if (subscribed == true || saveOnly)
        {
            using var count = HubStore.Command(db, "SELECT (SELECT COUNT(*) FROM external_watches WHERE user=$0 AND NOT(kind=$1 AND external_id=$2))+(SELECT COUNT(*) FROM subscriptions WHERE user=$0 AND item<>$3 AND item NOT IN (SELECT native FROM external_watches WHERE user=$0 AND native IS NOT NULL))", U(user), key.Kind, key.Id, native?.ToString("N") ?? ""); count.Transaction = tx;
            if ((long)count.ExecuteScalar()! >= 200) throw new HubError("Your watchlist is full (200 items).");
            using var save = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,$5,$4) ON CONFLICT(user,kind,external_id) DO UPDATE SET subscribed=CASE WHEN $6=1 THEN external_watches.subscribed ELSE excluded.subscribed END,title=excluded.title,native=COALESCE(excluded.native,external_watches.native)", U(user), key.Kind, key.Id, title, native?.ToString("N"), subscribed == true ? 1 : 0, saveOnly ? 1 : 0); save.Transaction = tx; save.ExecuteNonQuery();
        }
        else
        {
            using var mute = HubStore.Command(db, "UPDATE external_watches SET subscribed=0 WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); mute.Transaction = tx; mute.ExecuteNonQuery();
        }
        if (subscribed != true && !saveOnly)
        {
            ClearAlerts(db, tx, user, key.Kind + ":" + key.Id);
            if (subscribed == false) return;
        }
        using var intent = HubStore.Command(db, "INSERT INTO watchlist_intents VALUES($0,$1,$2,$3,0,'ready') ON CONFLICT(user,kind,external_id) DO UPDATE SET desired=excluded.desired,revision=watchlist_intents.revision+1,state='ready'", U(user), key.Kind, key.Id, subscribed is null ? 0 : 1); intent.Transaction = tx; intent.ExecuteNonQuery();
    }
    internal static void ClearAlerts(SqliteConnection db, SqliteTransaction tx, User user, string key)
    {
        foreach (var table in new[] { "media_notifications", "notification_baselines" })
        { using var clear = HubStore.Command(db, "DELETE FROM " + table + " WHERE user=$0 AND item=$1", U(user), key); clear.Transaction = tx; clear.ExecuteNonQuery(); }
    }
    public IReadOnlyList<ExternalFollow> List(User user)
    {
        var native = NativeWatches(user);
        var rows = new List<ExternalFollow>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT kind,external_id,title,subscribed,native FROM external_watches WHERE user=$0 ORDER BY kind,external_id LIMIT 201", U(user)))
        using (var r = read.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0, r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4))));
        if (rows.Count > 200) throw new HubError("Retained watchlist exceeds its safe size.");
        return rows.Select(row => (Row: row, Access: media.Resolve(user, new(row.Kind, row.ExternalId))))
            .Where(pair => pair.Access.Disclosable).Select(pair => pair.Row with { NativeId = pair.Access.Native,
                NativeIds = native.Rows.Where(r => r.Identity?.Key == new MediaKey(pair.Row.Kind, pair.Row.ExternalId)).Select(r => r.Id)
                    .Concat(pair.Access.Native is Guid id ? [id] : Array.Empty<Guid>()).Distinct().ToArray() }).ToArray();
    }
    public async Task<ActivityReceipt?> Follow(User user, MediaKey key, bool? subscribed, long revision, Func<Task> verifyActor, CancellationToken cancellation, bool saveOnly = false)
    {
        MediaCatalog.Kind(key.Kind); if (key.Id <= 0) throw new HubError("Invalid media ID.");
        var access = media.Resolve(user, key); if (!access.Disclosable) throw new HubError("Item unavailable.");
        using var scope = await bridge.Open(user, cancellation); var details = await scope.Get(key.Kind + "/" + key.Id, cancellation);
        if (details["id"]?.GetValue<int>() != key.Id) throw new HubError("Upstream watch identity changed.");
        var title = details["title"]?.GetValue<string>() ?? details["name"]?.GetValue<string>() ?? "Untitled";
        var aliases = NativeAliases(user, key);
        await verifyActor();
        using var db = store.Open(); using var tx = db.BeginTransaction();
        Protection.Check(db, tx, "watches");
        Touch(db, tx, user);
        using var check = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", U(user)); check.Transaction = tx;
        if ((long)check.ExecuteScalar()! != revision + 1) throw new HubError("Your watchlist changed. Refresh before retrying.");
        Set(db, tx, user, key, title, access.Native, subscribed, saveOnly);
        if (!saveOnly) Mirror(db, tx, user, aliases, subscribed);
        if (access.Native is Guid native)
        {
            using var mirror = HubStore.Command(db, saveOnly ? "INSERT INTO subscriptions VALUES($0,$1,0,$2) ON CONFLICT(user,item) DO NOTHING" : subscribed is null ? "DELETE FROM subscriptions WHERE user=$0 AND item=$1" :
                subscribed == false ? "UPDATE subscriptions SET subscribed=0 WHERE user=$0 AND item=$1" : "INSERT INTO subscriptions VALUES($0,$1,1,$2) ON CONFLICT(user,item) DO UPDATE SET subscribed=1", U(user), native.ToString("N"), HubStore.Now);
            mirror.Transaction = tx; mirror.ExecuteNonQuery();
        }
        Protection.Seal(db, tx, "watches"); tx.Commit();
        return (subscribed == true || saveOnly) && access.Native is Guid item ? activity?.Record(user, item, MediaInteraction.Follow) : null;
    }
    internal static HashSet<MediaKey> Plan(HashSet<MediaKey> local, HashSet<MediaKey> remote, HashSet<MediaKey>? baseline, IEnumerable<(MediaKey Key, bool Desired)> intents)
    {
        var desired = local.Union(remote).ToHashSet();
        if (baseline is not null) desired.ExceptWith(baseline.Except(local).Union(baseline.Except(remote)));
        foreach (var intent in intents) { if (intent.Desired) desired.Add(intent.Key); else desired.Remove(intent.Key); }
        return desired;
    }
    private static async Task<Dictionary<MediaKey, string>> Read(SeerrScope scope, CancellationToken cancellation)
    {
        var items = new Dictionary<MediaKey, string>(); int? total = null, pages = null;
        for (var page = 1; page <= 50; page++)
        {
            var result = await scope.Get("discover/watchlist?page=" + page, cancellation);
            var count = result["totalResults"]!.GetValue<int>(); var batches = result["totalPages"]!.GetValue<int>(); var rows = result["results"]!.AsArray();
            if (count is < 0 or > 1000 || batches is < 0 or > 50 || batches != (count + 19) / 20 && !(count == 0 && batches == 1) ||
                result["page"]!.GetValue<int>() != page || total is not null && (total != count || pages != batches) || rows.Count != Math.Min(20, Math.Max(0, count - (page - 1) * 20))) throw new HubError("Watchlist scan incomplete. No removals were inferred.");
            total = count; pages = batches;
            foreach (var row in rows)
            {
                var key = new MediaKey(row!["mediaType"]!.GetValue<string>(), row["tmdbId"]!.GetValue<int>()); MediaCatalog.Kind(key.Kind);
                if (key.Id <= 0 || row["requestedBy"] is JsonNode owner && owner["id"]?.GetValue<int>() != scope.Viewer.Id ||
                    !items.TryAdd(key, row["title"]!.GetValue<string>())) throw new HubError("Invalid watchlist identity.");
            }
            if (page >= Math.Max(1, batches)) return items;
        }
        throw new HubError("Watchlist scan is too large.");
    }
    private static async Task<Dictionary<MediaKey, string>> Stable(SeerrScope scope, CancellationToken cancellation)
    {
        var a = await Read(scope, cancellation); var b = await Read(scope, cancellation);
        if (!a.Keys.ToHashSet().SetEquals(b.Keys)) throw new HubError("Watchlist changed. Try again."); return b;
    }
    public async Task<WatchSyncResult> Sync(User user, Func<Task> verifyActor, CancellationToken cancellation, bool importNative = true)
    {
        Protection.Check("watches");
        if (Protection.NeedsWatchReview(U(user))) throw new HubError("Review your retained watchlist before enabling reconciliation. Older intents are not authorized to send.");
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock");
        var native = NativeWatches(user);
        using var scope = await bridge.Open(user, cancellation); var remote = await Stable(scope, cancellation);
        await verifyActor(); await scope.Verify(cancellation);
        var pending = new List<(MediaKey Key, bool Desired, long Revision, string State)>();
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            Protection.Check(db, tx, "watches");
            CheckRevision(db, tx, user, native.Revision);
            var local = new Dictionary<MediaKey, string>();
            using (var read = HubStore.Command(db, "SELECT kind,external_id,title FROM external_watches WHERE user=$0 LIMIT 201", U(user)))
            { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) local.Add(new(r.GetString(0), r.GetInt32(1)), r.GetString(2)); }
            using (var read = HubStore.Command(db, "SELECT kind,external_id,desired,revision,state FROM watchlist_intents WHERE user=$0 LIMIT 1001", U(user)))
            { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) { if (r.GetInt32(2) is not (0 or 1) || r.GetInt64(3) < 0 || r.GetString(4) is not ("ready" or "sending" or "uncertain")) throw new HubError("Invalid retained watchlist intent."); pending.Add((new(r.GetString(0), r.GetInt32(1)), r.GetInt32(2) != 0, r.GetInt64(3), r.GetString(4))); } }
            using var baseline = HubStore.Command(db, "SELECT payload FROM watchlist_baselines WHERE user=$0", U(user)); baseline.Transaction = tx; var saved = baseline.ExecuteScalar() as string;
            var baselineKeys = saved is null ? null : SafeJson.Read<HashSet<MediaKey>>(saved);
            if (local.Count > 200 || pending.Count > 1000 || baselineKeys?.Count > 1000) throw new HubError("Retained watchlist exceeds its safe budget.");
            foreach (var key in local.Keys.Concat(pending.Select(p => p.Key)).Concat(baselineKeys ?? [])) { if (key is null || key.Id <= 0) throw new HubError("Invalid retained watchlist identity."); MediaCatalog.Kind(key.Kind); }
            var imported = false;
            foreach (var group in native.Rows.Where(r => importNative && r.Identity is not null).GroupBy(r => r.Identity!.Value.Key))
            {
                if (local.ContainsKey(group.Key) || pending.Any(p => p.Key == group.Key && !p.Desired)) continue;
                var first = group.First(); var title = first.Identity!.Value.Title;
                MediaCatalog.Kind(group.Key.Kind); if (group.Key.Id <= 0) throw new HubError("Invalid native watch identity.");
                using var add = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,$4,$5)", U(user), group.Key.Kind, group.Key.Id, title, group.Any(r => r.Subscribed) ? 1 : 0, group.Count() == 1 ? first.Id.ToString("N") : null);
                add.Transaction = tx; add.ExecuteNonQuery(); local.Add(group.Key, title); imported = true;
            }
            if (local.Count > 200) throw new HubError("Combined watchlist exceeds 200 titles. Neither list was replaced.");
            var desired = Plan(local.Keys.ToHashSet(), remote.Keys.ToHashSet(), baselineKeys, pending.Select(p => (p.Key, p.Desired)));
            var nativeOnly = native.Rows.Count(r => r.Identity is null);
            if (desired.Count + nativeOnly > 200) throw new HubError("Combined watchlist exceeds 200 titles. Neither list was replaced.");
            foreach (var key in desired.SymmetricExcept(remote.Keys.ToHashSet()))
            {
                if (pending.Any(p => p.Key == key)) continue;
                using var add = HubStore.Command(db, "INSERT INTO watchlist_intents VALUES($0,$1,$2,$3,0,'ready')", U(user), key.Kind, key.Id, desired.Contains(key) ? 1 : 0); add.Transaction = tx; add.ExecuteNonQuery();
                pending.Add((key, desired.Contains(key), 0, "ready"));
            }
            foreach (var key in local.Keys.Except(desired))
            {
                ClearAlerts(db, tx, user, key.Kind + ":" + key.Id);
                Mirror(db, tx, user, native.Rows.Where(r => r.Identity?.Key == key).Select(r => r.Id), null);
                using var mirror = HubStore.Command(db, "DELETE FROM subscriptions WHERE user=$0 AND item IN (SELECT native FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2)", U(user), key.Kind, key.Id); mirror.Transaction = tx; mirror.ExecuteNonQuery();
                using var remove = HubStore.Command(db, "DELETE FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); remove.Transaction = tx; remove.ExecuteNonQuery();
            }
            foreach (var key in desired.Except(local.Keys))
            {
                using var add = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,0,NULL)", U(user), key.Kind, key.Id, remote.GetValueOrDefault(key) ?? "Untitled"); add.Transaction = tx; add.ExecuteNonQuery();
            }
            if (imported || !local.Keys.ToHashSet().SetEquals(desired)) Touch(db, tx, user);
            using var save = HubStore.Command(db, "INSERT INTO watchlist_baselines VALUES($0,$1) ON CONFLICT(user) DO UPDATE SET payload=excluded.payload", U(user), JsonSerializer.Serialize(desired)); save.Transaction = tx; save.ExecuteNonQuery(); Protection.Seal(db, tx, "watches"); tx.Commit();
        }
        foreach (var intent in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            if (remote.ContainsKey(intent.Key) == intent.Desired || intent.State != "ready") continue;
            await verifyActor(); await scope.Verify(cancellation);
            // Recheck library restrictions before any outbound membership write.
            if (!media.Resolve(user, intent.Key).Disclosable) continue;
            var claimed = Protection.Update("watches", (db, tx) => { using var claim = HubStore.Command(db, "UPDATE watchlist_intents SET state='sending' WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND desired=$4 AND state='ready'", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision, intent.Desired ? 1 : 0); claim.Transaction = tx; return claim.ExecuteNonQuery(); });
            if (claimed != 1) continue;
            try
            {
                if (intent.Desired)
                {
                    var details = await scope.Get(intent.Key.Kind + "/" + intent.Key.Id, cancellation);
                    if (details["id"]?.GetValue<int>() != intent.Key.Id) throw new HubError("Upstream watch identity changed.");
                    await scope.Write(HttpMethod.Post, "watchlist", new JsonObject { ["mediaType"] = intent.Key.Kind, ["tmdbId"] = intent.Key.Id, ["title"] = details["title"]?.GetValue<string>() ?? details["name"]?.GetValue<string>() ?? "Untitled" }, cancellation);
                }
                else await scope.Write(HttpMethod.Delete, "watchlist/" + intent.Key.Id + "?mediaType=" + intent.Key.Kind, null, cancellation);
            }
            catch { /* Read reconciliation only; never automatically repeat an uncertain mutation. */ }
            Protection.Update("watches", (db, tx) => { using var uncertain = HubStore.Command(db, "UPDATE watchlist_intents SET state='uncertain' WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND state='sending'", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision); uncertain.Transaction = tx; return uncertain.ExecuteNonQuery(); });
            cancellation.ThrowIfCancellationRequested();
        }
        var confirmed = await Stable(scope, cancellation);
        await verifyActor(); await scope.Verify(cancellation);
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            Protection.Check(db, tx, "watches");
            foreach (var intent in pending.Where(p => confirmed.ContainsKey(p.Key) == p.Desired))
            {
                using var clear = HubStore.Command(db, "DELETE FROM watchlist_intents WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND desired=$4", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision, intent.Desired ? 1 : 0); clear.Transaction = tx; clear.ExecuteNonQuery();
            }
            using var count = HubStore.Command(db, "SELECT COUNT(*) FROM watchlist_intents WHERE user=$0", U(user)); count.Transaction = tx;
            var remaining = (int)(long)count.ExecuteScalar()!; Protection.Seal(db, tx, "watches"); tx.Commit(); return new(remaining == 0, remaining);
        }
    }
    public IReadOnlyList<WatchSyncIssue> Issues(User user)
    {
        Protection.Check("watches");
        var rows = new List<WatchSyncIssue>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT i.kind,i.external_id,w.title,i.state,i.desired,i.revision FROM watchlist_intents i LEFT JOIN external_watches w ON w.user=i.user AND w.kind=i.kind AND w.external_id=i.external_id WHERE i.user=$0 ORDER BY i.kind,i.external_id LIMIT 1001", U(user)))
        using (var r = read.ExecuteReader()) while (r.Read())
        {
            var kind = r.GetString(0); var id = r.GetInt32(1); var state = r.GetString(3); MediaCatalog.Kind(kind);
            if (id <= 0 || state is not ("ready" or "sending" or "uncertain") || r.GetInt32(4) is not (0 or 1) || r.GetInt64(5) < 0) throw new HubError("Invalid retained watchlist intent. No state was reset.");
            rows.Add(new(kind, id, r.IsDBNull(2) ? "Saved watchlist change" : r.GetString(2), state, r.GetInt32(4) == 1, r.GetInt64(5)));
        }
        if (rows.Count > 1000) throw new HubError("Retained watchlist exceeds its safe budget.");
        return rows.Where(row => media.Resolve(user, new(row.Kind, row.ExternalId)).Disclosable).ToArray();
    }
    public async Task<WatchSyncReview> Review(User user, MediaKey key, Func<Task> verifyActor, CancellationToken cancellation)
    {
        var config = settings?.Current ?? throw new HubError("Reviewed recovery is unavailable.");
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock");
        var issue = Issues(user).SingleOrDefault(r => r.Kind == key.Kind && r.ExternalId == key.Id) ?? throw new HubError("Watchlist change unavailable.");
        if (issue.State == "ready") throw new HubError("This change has not been sent. Sync normally before reviewing an uncertain outcome.");
        var native = NativeWatches(user); using var scope = await bridge.Open(user, cancellation);
        var remote = await Stable(scope, cancellation); await verifyActor(); await scope.Verify(cancellation);
        if (settings!.Current.Revision != config.Revision || !media.Resolve(user, key).Disclosable) throw new HubError("Configuration or visibility changed. Review again.");
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var proof = new Recovery(user.Id, scope.Viewer.Origin, scope.Viewer.Id, config.Revision, key, native.Revision, issue.Revision, issue.State, issue.Desired, remote.ContainsKey(key), expires);
        var title = remote.GetValueOrDefault(key) ?? issue.Title;
        return new(settings.Protect(JsonSerializer.Serialize(proof), RecoveryPurpose), title, List(user).Any(r => r.Kind == key.Kind && r.ExternalId == key.Id), proof.Remote, expires);
    }
    public async Task<WatchSyncResult> AcceptRemote(User user, string token, Func<Task> verifyActor, CancellationToken cancellation)
    {
        if (settings is null || token is null || token.Length > 8192) throw new HubError("Invalid review proof.");
        Recovery proof;
        try { proof = SafeJson.Read<Recovery>(settings.Unprotect(token, RecoveryPurpose), 4096); }
        catch { throw new HubError("Invalid review proof. No state was changed."); }
        if (proof.User != user.Id || proof.Key is null || proof.Expires <= DateTimeOffset.UtcNow || proof.Expires > DateTimeOffset.UtcNow.AddMinutes(5) || settings.Current.Revision != proof.Configuration) throw new HubError("Review expired or identity/configuration changed. Review again.");
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock");
        var issue = Issues(user).SingleOrDefault(r => r.Kind == proof.Key.Kind && r.ExternalId == proof.Key.Id);
        if (issue is null || issue.Revision != proof.IntentRevision || issue.State != proof.State || issue.Desired != proof.Desired) throw new HubError("Watchlist change no longer matches this review.");
        var native = NativeWatches(user); if (native.Revision != proof.PersonalRevision) throw new HubError("Your watchlist changed. Review again.");
        using var scope = await bridge.Open(user, cancellation);
        if (scope.Viewer.Origin != proof.Origin || scope.Viewer.Id != proof.Seerr) throw new HubError("Seerr identity changed. Review again.");
        var remote = await Stable(scope, cancellation); await verifyActor(); await scope.Verify(cancellation);
        if (remote.ContainsKey(proof.Key) != proof.Remote || settings.Current.Revision != proof.Configuration || !media.Resolve(user, proof.Key).Disclosable) throw new HubError("Remote membership, configuration or visibility changed. Review again.");
        using var db = store.Open(); using var tx = db.BeginTransaction(); Protection.Check(db, tx, "watches"); CheckRevision(db, tx, user, proof.PersonalRevision);
        using var remove = HubStore.Command(db, "DELETE FROM watchlist_intents WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND state=$4 AND desired=$5", U(user), proof.Key.Kind, proof.Key.Id, proof.IntentRevision, proof.State, proof.Desired ? 1 : 0); remove.Transaction = tx;
        if (remove.ExecuteNonQuery() != 1) throw new HubError("Watchlist change no longer matches this review.");
        if (proof.Remote)
        {
            using var add = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,0,NULL) ON CONFLICT(user,kind,external_id) DO NOTHING", U(user), proof.Key.Kind, proof.Key.Id, remote[proof.Key]); add.Transaction = tx; add.ExecuteNonQuery();
        }
        else
        {
            using var drop = HubStore.Command(db, "DELETE FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), proof.Key.Kind, proof.Key.Id); drop.Transaction = tx; drop.ExecuteNonQuery();
            Mirror(db, tx, user, native.Rows.Where(r => r.Identity?.Key == proof.Key).Select(r => r.Id), null);
            ClearAlerts(db, tx, user, proof.Key.Kind + ":" + proof.Key.Id);
        }
        using var baseline = HubStore.Command(db, "SELECT payload FROM watchlist_baselines WHERE user=$0", U(user)); baseline.Transaction = tx;
        var saved = baseline.ExecuteScalar() as string; var keys = saved is null ? new HashSet<MediaKey>() : SafeJson.Read<HashSet<MediaKey>>(saved);
        if (keys.Count > 1000 || keys.Any(k => k is null || k.Id <= 0 || k.Kind is not ("movie" or "tv"))) throw new HubError("Invalid retained watchlist baseline. Nothing was reset.");
        if (proof.Remote) keys.Add(proof.Key); else keys.Remove(proof.Key);
        using var save = HubStore.Command(db, "INSERT INTO watchlist_baselines VALUES($0,$1) ON CONFLICT(user) DO UPDATE SET payload=excluded.payload", U(user), JsonSerializer.Serialize(keys)); save.Transaction = tx; save.ExecuteNonQuery();
        Touch(db, tx, user); using var count = HubStore.Command(db, "SELECT COUNT(*) FROM watchlist_intents WHERE user=$0", U(user)); count.Transaction = tx; var remaining = (int)(long)count.ExecuteScalar()!;
        Protection.Seal(db, tx, "watches"); tx.Commit(); return new(remaining == 0, remaining); // Local adoption only: NEVER sends a retry to Seerr.
    }
    private sealed record LegacyProof(Guid User, string Origin, int Seerr, string Fingerprint, DateTimeOffset Expires);
    public async Task<LegacyWatchReview> ReviewLegacy(User user, Func<Task> verifyActor, CancellationToken cancellation)
    {
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock");
        if (!Protection.NeedsWatchReview(U(user))) throw new HubError("Your watchlist does not need legacy review.");
        var native = NativeWatches(user); var visible = List(user);
        if (native.Rows.Any(row => nativeMedia?.Find(user, row.Id) is null)) throw new HubError("A retained native watch is not currently accessible. It was preserved; restore visibility or a verified backup before approving this list.");
        using var scope = await bridge.Open(user, cancellation); _ = await Stable(scope, cancellation); await verifyActor(); await scope.Verify(cancellation);
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true);
        using var count = HubStore.Command(db, "SELECT COUNT(*) FROM external_watches WHERE user=$0", U(user)); count.Transaction = tx;
        if ((long)count.ExecuteScalar()! != visible.Count) throw new HubError("Some retained external titles are inaccessible. Nothing was authorized or removed.");
        var expires = DateTimeOffset.UtcNow.AddMinutes(5); var proof = new LegacyProof(user.Id, scope.Viewer.Origin, scope.Viewer.Id, Protection.Fingerprint(db, tx, "watches"), expires); tx.Commit();
        return new(Configuration.Protect(JsonSerializer.Serialize(proof), "MediaHub.LegacyWatchReview.v1"), visible.Select(r => r.Title).Concat(native.Rows.Select(r => nativeMedia!.Find(user, r.Id)!.Title)).Distinct().ToArray(), expires);
    }
    public async Task AcceptLegacy(User user, string token, Func<Task> verifyActor, CancellationToken cancellation)
    {
        if (token.Length > 8192) throw new HubError("Invalid retained-list review proof.");
        var proof = SafeJson.Read<LegacyProof>(Configuration.Unprotect(token, "MediaHub.LegacyWatchReview.v1"), 4096);
        if (proof.User != user.Id || proof.Expires <= DateTimeOffset.UtcNow || proof.Expires > DateTimeOffset.UtcNow.AddMinutes(5)) throw new HubError("Retained-list review expired or changed accounts.");
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock"); using var scope = await bridge.Open(user, cancellation);
        await verifyActor(); await scope.Verify(cancellation);
        if (scope.Viewer.Origin != proof.Origin || scope.Viewer.Id != proof.Seerr || !Protection.NeedsWatchReview(U(user))) throw new HubError("Retained-list identity or review changed.");
        if (NativeWatches(user).Rows.Any(row => nativeMedia?.Find(user, row.Id) is null)) throw new HubError("Retained native visibility changed. Review again; nothing was authorized.");
        var visible = List(user);
        Protection.Update("watches", (db, tx) =>
        {
            if (Protection.Fingerprint(db, tx, "watches") != proof.Fingerprint) throw new HubError("Your retained list changed. Review again.");
            using var count = HubStore.Command(db, "SELECT COUNT(*) FROM external_watches WHERE user=$0", U(user)); count.Transaction = tx;
            if ((long)count.ExecuteScalar()! != visible.Count) throw new HubError("Retained external visibility changed. Review again.");
            using var read = HubStore.Command(db, "SELECT payload FROM watchlist_baselines WHERE user=$0", U(user)); read.Transaction = tx; var baseline = read.ExecuteScalar() as string;
            var intents = new List<object>();
            using (var history = HubStore.Command(db, "SELECT kind,external_id,desired,revision,state FROM watchlist_intents WHERE user=$0 LIMIT 201", U(user))) { history.Transaction = tx; using var r = history.ExecuteReader(); while (r.Read()) intents.Add(new { Kind = r.GetString(0), ExternalId = r.GetInt32(1), Desired = r.GetInt64(2), Revision = r.GetInt64(3), State = r.GetString(4) }); }
            if (intents.Count > 200) throw new HubError("Retained intent history exceeds its review budget. Nothing was changed.");
            using var archive = HubStore.Command(db, "INSERT INTO watch_reviews VALUES($0,$1)", U(user), JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, PreviousBaseline = baseline, PreviousIntents = intents, Explanation = "Explicit union review; no upstream write and no old intent retry" })); archive.Transaction = tx; archive.ExecuteNonQuery();
            using var stop = HubStore.Command(db, "UPDATE watchlist_intents SET state='uncertain' WHERE user=$0", U(user)); stop.Transaction = tx; stop.ExecuteNonQuery();
            using var union = HubStore.Command(db, "DELETE FROM watchlist_baselines WHERE user=$0", U(user)); union.Transaction = tx; union.ExecuteNonQuery(); Touch(db, tx, user); return true;
        }); // Only an explicit later Sync may union memberships; uncertain writes are never resent.
    }
}

internal static class WatchlistSetExtensions
{
    internal static HashSet<T> SymmetricExcept<T>(this HashSet<T> a, HashSet<T> b) { var result = a.ToHashSet(); result.SymmetricExceptWith(b); return result; }
}
