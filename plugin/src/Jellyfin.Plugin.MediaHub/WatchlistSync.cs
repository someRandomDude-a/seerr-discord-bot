using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record MediaKey(string Kind, int Id);
public sealed record ExternalFollow(string Kind, int ExternalId, string Title, bool Subscribed, Guid? NativeId);
public sealed record WatchSyncResult(bool Complete, int Pending);
public interface IExternalMedia
{
    (MediaKey Key, string Title)? Identify(User user, Guid native);
    (bool Disclosable, Guid? Native) Resolve(User user, MediaKey key);
}
public sealed class ExternalMedia(ILibraryManager library) : IExternalMedia
{
    public (MediaKey Key, string Title)? Identify(User user, Guid native)
    {
        var item = library.GetItemById(native); if (item is null || !item.IsVisibleStandalone(user)) throw new HubError("Item unavailable.");
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
public sealed class WatchlistSync(HubStore store, SeerrBridge bridge, IExternalMedia media, MediaActivity? activity = null)
{
    private static string U(User user) => user.Id.ToString("N");
    internal static void Touch(SqliteConnection db, SqliteTransaction tx, User user)
    {
        using var ensure = HubStore.Command(db, "INSERT OR IGNORE INTO personal VALUES($0,0,$1)", U(user), JsonSerializer.Serialize(new PersonalPreferences())); ensure.Transaction = tx; ensure.ExecuteNonQuery();
        using var bump = HubStore.Command(db, "UPDATE personal SET revision=revision+1 WHERE user=$0", U(user)); bump.Transaction = tx; bump.ExecuteNonQuery();
    }
    internal void NativeIntent(SqliteConnection db, SqliteTransaction tx, User user, Guid native, (MediaKey Key, string Title)? identity, bool? subscribed)
    {
        if (identity is not { } mapped) return;
        Set(db, tx, user, mapped.Key, mapped.Title, native, subscribed);
    }
    internal (MediaKey Key, string Title)? Identify(User user, Guid native) => media.Identify(user, native);
    internal void RemoveNative(SqliteConnection db, SqliteTransaction tx, User user, Guid native)
    {
        using var read = HubStore.Command(db, "SELECT kind,external_id,title FROM external_watches WHERE user=$0 AND native=$1", U(user), native.ToString("N")); read.Transaction = tx;
        var rows = new List<(MediaKey Key, string Title)>();
        using (var r = read.ExecuteReader()) while (r.Read()) rows.Add((new(r.GetString(0), r.GetInt32(1)), r.GetString(2)));
        foreach (var row in rows) Set(db, tx, user, row.Key, row.Title, native, null);
    }
    private static void Set(SqliteConnection db, SqliteTransaction tx, User user, MediaKey key, string title, Guid? native, bool? subscribed)
    {
        if (subscribed is null)
        {
            using var remove = HubStore.Command(db, "DELETE FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); remove.Transaction = tx; remove.ExecuteNonQuery();
        }
        else if (subscribed == true)
        {
            using var count = HubStore.Command(db, "SELECT (SELECT COUNT(*) FROM external_watches WHERE user=$0 AND NOT(kind=$1 AND external_id=$2))+(SELECT COUNT(*) FROM subscriptions WHERE user=$0 AND item<>$3 AND item NOT IN (SELECT native FROM external_watches WHERE user=$0 AND native IS NOT NULL))", U(user), key.Kind, key.Id, native?.ToString("N") ?? ""); count.Transaction = tx;
            if ((long)count.ExecuteScalar()! >= 200) throw new HubError("Your watchlist is full (200 items).");
            using var save = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,1,$4) ON CONFLICT(user,kind,external_id) DO UPDATE SET subscribed=1,title=excluded.title,native=COALESCE(excluded.native,external_watches.native)", U(user), key.Kind, key.Id, title, native?.ToString("N")); save.Transaction = tx; save.ExecuteNonQuery();
        }
        else
        {
            using var mute = HubStore.Command(db, "UPDATE external_watches SET subscribed=0 WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); mute.Transaction = tx; mute.ExecuteNonQuery();
        }
        if (subscribed != true)
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
        var rows = new List<ExternalFollow>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT kind,external_id,title,subscribed,native FROM external_watches WHERE user=$0 ORDER BY kind,external_id LIMIT 201", U(user)))
        using (var r = read.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0, r.IsDBNull(4) ? null : Guid.Parse(r.GetString(4))));
        if (rows.Count > 200) throw new HubError("Retained watchlist exceeds its safe size.");
        return rows.Select(row => (Row: row, Access: media.Resolve(user, new(row.Kind, row.ExternalId))))
            .Where(pair => pair.Access.Disclosable).Select(pair => pair.Row with { NativeId = pair.Access.Native }).ToArray();
    }
    public async Task<ActivityReceipt?> Follow(User user, MediaKey key, bool? subscribed, long revision, Func<Task> verifyActor, CancellationToken cancellation)
    {
        MediaCatalog.Kind(key.Kind); if (key.Id <= 0) throw new HubError("Invalid media ID.");
        var access = media.Resolve(user, key); if (!access.Disclosable) throw new HubError("Item unavailable.");
        using var scope = await bridge.Open(user, cancellation); var details = await scope.Get(key.Kind + "/" + key.Id, cancellation);
        if (details["id"]?.GetValue<int>() != key.Id) throw new HubError("Upstream watch identity changed.");
        var title = details["title"]?.GetValue<string>() ?? details["name"]?.GetValue<string>() ?? "Untitled";
        await verifyActor();
        using var db = store.Open(); using var tx = db.BeginTransaction();
        Touch(db, tx, user);
        using var check = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", U(user)); check.Transaction = tx;
        if ((long)check.ExecuteScalar()! != revision + 1) throw new HubError("Your watchlist changed. Refresh before retrying.");
        Set(db, tx, user, key, title, access.Native, subscribed);
        if (access.Native is Guid native)
        {
            using var mirror = HubStore.Command(db, subscribed is null ? "DELETE FROM subscriptions WHERE user=$0 AND item=$1" :
                subscribed == false ? "UPDATE subscriptions SET subscribed=0 WHERE user=$0 AND item=$1" : "INSERT INTO subscriptions VALUES($0,$1,1,$2) ON CONFLICT(user,item) DO UPDATE SET subscribed=1", U(user), native.ToString("N"), HubStore.Now);
            mirror.Transaction = tx; mirror.ExecuteNonQuery();
        }
        tx.Commit();
        return subscribed == true && access.Native is Guid item ? activity?.Record(user, item, MediaInteraction.Follow) : null;
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
    public async Task<WatchSyncResult> Sync(User user, Func<Task> verifyActor, CancellationToken cancellation)
    {
        using var lease = StateFiles.Lease(store.DirectoryPath, "watch-" + U(user) + ".lock");
        using var scope = await bridge.Open(user, cancellation); var remote = await Stable(scope, cancellation);
        var pending = new List<(MediaKey Key, bool Desired, long Revision, string State)>();
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            var local = new Dictionary<MediaKey, string>();
            using (var read = HubStore.Command(db, "SELECT kind,external_id,title FROM external_watches WHERE user=$0 LIMIT 201", U(user)))
            { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) local.Add(new(r.GetString(0), r.GetInt32(1)), r.GetString(2)); }
            using (var read = HubStore.Command(db, "SELECT kind,external_id,desired,revision,state FROM watchlist_intents WHERE user=$0 LIMIT 1001", U(user)))
            { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) { if (r.GetInt32(2) is not (0 or 1) || r.GetInt64(3) < 0 || r.GetString(4) is not ("ready" or "sending" or "uncertain")) throw new HubError("Invalid retained watchlist intent."); pending.Add((new(r.GetString(0), r.GetInt32(1)), r.GetInt32(2) != 0, r.GetInt64(3), r.GetString(4))); } }
            using var baseline = HubStore.Command(db, "SELECT payload FROM watchlist_baselines WHERE user=$0", U(user)); baseline.Transaction = tx; var saved = baseline.ExecuteScalar() as string;
            var baselineKeys = saved is null ? null : SafeJson.Read<HashSet<MediaKey>>(saved);
            if (local.Count > 200 || pending.Count > 1000 || baselineKeys?.Count > 1000) throw new HubError("Retained watchlist exceeds its safe budget.");
            foreach (var key in local.Keys.Concat(pending.Select(p => p.Key)).Concat(baselineKeys ?? [])) { if (key is null || key.Id <= 0) throw new HubError("Invalid retained watchlist identity."); MediaCatalog.Kind(key.Kind); }
            var desired = Plan(local.Keys.ToHashSet(), remote.Keys.ToHashSet(), baselineKeys, pending.Select(p => (p.Key, p.Desired)));
            using var extras = HubStore.Command(db, "SELECT COUNT(*) FROM subscriptions WHERE user=$0 AND item NOT IN (SELECT native FROM external_watches WHERE user=$0 AND native IS NOT NULL)", U(user)); extras.Transaction = tx;
            if (desired.Count + (long)extras.ExecuteScalar()! > 200) throw new HubError("Combined watchlist exceeds 200 titles. Neither list was replaced.");
            foreach (var key in desired.SymmetricExcept(remote.Keys.ToHashSet()))
            {
                if (pending.Any(p => p.Key == key)) continue;
                using var add = HubStore.Command(db, "INSERT INTO watchlist_intents VALUES($0,$1,$2,$3,0,'ready')", U(user), key.Kind, key.Id, desired.Contains(key) ? 1 : 0); add.Transaction = tx; add.ExecuteNonQuery();
                pending.Add((key, desired.Contains(key), 0, "ready"));
            }
            foreach (var key in local.Keys.Except(desired))
            {
                ClearAlerts(db, tx, user, key.Kind + ":" + key.Id);
                using var mirror = HubStore.Command(db, "DELETE FROM subscriptions WHERE user=$0 AND item IN (SELECT native FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2)", U(user), key.Kind, key.Id); mirror.Transaction = tx; mirror.ExecuteNonQuery();
                using var remove = HubStore.Command(db, "DELETE FROM external_watches WHERE user=$0 AND kind=$1 AND external_id=$2", U(user), key.Kind, key.Id); remove.Transaction = tx; remove.ExecuteNonQuery();
            }
            foreach (var key in desired.Except(local.Keys))
            {
                using var add = HubStore.Command(db, "INSERT INTO external_watches VALUES($0,$1,$2,$3,0,NULL)", U(user), key.Kind, key.Id, remote.GetValueOrDefault(key) ?? "Untitled"); add.Transaction = tx; add.ExecuteNonQuery();
            }
            if (!local.Keys.ToHashSet().SetEquals(desired)) Touch(db, tx, user);
            using var save = HubStore.Command(db, "INSERT INTO watchlist_baselines VALUES($0,$1) ON CONFLICT(user) DO UPDATE SET payload=excluded.payload", U(user), JsonSerializer.Serialize(desired)); save.Transaction = tx; save.ExecuteNonQuery(); tx.Commit();
        }
        foreach (var intent in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            if (remote.ContainsKey(intent.Key) == intent.Desired || intent.State != "ready") continue;
            await verifyActor(); await scope.Verify(cancellation);
            // Recheck library restrictions before any outbound membership write.
            if (!media.Resolve(user, intent.Key).Disclosable) continue;
            using (var db = store.Open()) using (var claim = HubStore.Command(db, "UPDATE watchlist_intents SET state='sending' WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND desired=$4 AND state='ready'", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision, intent.Desired ? 1 : 0))
                if (claim.ExecuteNonQuery() != 1) continue;
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
            using (var db = store.Open()) using (var uncertain = HubStore.Command(db, "UPDATE watchlist_intents SET state='uncertain' WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND state='sending'", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision)) uncertain.ExecuteNonQuery();
            cancellation.ThrowIfCancellationRequested();
        }
        var confirmed = await Stable(scope, cancellation);
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            foreach (var intent in pending.Where(p => confirmed.ContainsKey(p.Key) == p.Desired))
            {
                using var clear = HubStore.Command(db, "DELETE FROM watchlist_intents WHERE user=$0 AND kind=$1 AND external_id=$2 AND revision=$3 AND desired=$4", U(user), intent.Key.Kind, intent.Key.Id, intent.Revision, intent.Desired ? 1 : 0); clear.Transaction = tx; clear.ExecuteNonQuery();
            }
            using var count = HubStore.Command(db, "SELECT COUNT(*) FROM watchlist_intents WHERE user=$0", U(user)); count.Transaction = tx;
            var remaining = (int)(long)count.ExecuteScalar()!; tx.Commit(); return new(remaining == 0, remaining);
        }
    }
}

internal static class WatchlistSetExtensions
{
    internal static HashSet<T> SymmetricExcept<T>(this HashSet<T> a, HashSet<T> b) { var result = a.ToHashSet(); result.SymmetricExceptWith(b); return result; }
}
