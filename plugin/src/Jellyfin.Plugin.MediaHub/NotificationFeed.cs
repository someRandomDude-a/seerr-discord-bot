using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record HubNotification(string Id, Guid? Item, string Title, string Status, DateTimeOffset Created, bool Seen);
public interface INotificationStatus
{
    Task<string?> Read(User user, string key, CancellationToken cancellation);
}
public sealed class NotificationStatus(MediaCatalog catalog, IViewerMedia media, ExtraMedia? extras = null) : INotificationStatus
{
    public async Task<string?> Read(User user, string key, CancellationToken cancellation)
    {
        if (Guid.TryParse(key, out var native)) return media.Find(user, native) is null ? null : "Available";
        var parts = key.Split(':');
        if (parts.Length == 2 && parts[0] is "music" or "book" && extras is not null) return (await extras.Details(user, parts[0], parts[1], false, cancellation)).Status;
        if (parts.Length != 2 || !int.TryParse(parts[1], out var id)) throw new HubError("Invalid notification target.");
        return (await catalog.Details(user, parts[0], id, cancellation)).Status; // Passive reads never record activity.
    }
}

/// <summary>Per-user, access-projected feed and at-most-once Discord send intents. No shared raw upstream payloads.</summary>
public sealed class NotificationFeed(HubStore store, PersonalService personal, INotificationStatus statuses, HostIdentity identity, SettingsService settings)
{
    private readonly ConcurrentDictionary<Guid, string> cursors = new();
    private string deliveryCursor = "";
    private sealed record Target(string Key, Guid? Native, string Title);
    private static string U(User user) => user.Id.ToString("N");
    private static IReadOnlyList<Target> Targets(PersonalState state)
    {
        var external = state.ExternalWatchlist ?? [];
        var extras = state.ExtraWatchlist ?? [];
        var mapped = external.Where(w => w.NativeId is not null).Select(w => w.NativeId).Concat(extras.Select(w => w.NativeId)).ToHashSet();
        return external.Where(w => w.Subscribed).Select(w => new Target(w.Kind + ":" + w.ExternalId, w.NativeId, w.Title))
            .Concat(extras.Where(w => w.Subscribed).Select(w => new Target(w.Kind + ":" + w.ForeignId, w.NativeId, w.Title)))
            .Concat(state.Watchlist.Where(w => w.Subscribed && !mapped.Contains(w.ItemId)).Select(w => new Target(w.ItemId.ToString("N"), w.ItemId, w.Title))).OrderBy(w => w.Key, StringComparer.Ordinal).ToArray();
    }
    public async Task Scan(User user, CancellationToken cancellation)
    {
        using var lease = StateFiles.Lease(store.DirectoryPath, "notifications-" + U(user) + ".lock");
        var state = personal.State(user); var targets = Targets(state); var cursor = cursors.GetValueOrDefault(user.Id) ?? "";
        var batch = targets.Where(t => string.CompareOrdinal(t.Key, cursor) > 0).Take(20).ToArray();
        if (batch.Length == 0) { cursors.TryRemove(user.Id, out _); batch = targets.Take(20).ToArray(); }
        foreach (var target in batch)
        {
            cancellation.ThrowIfCancellationRequested();
            var status = await statuses.Read(user, target.Key, cancellation); if (status is null) continue;
            // Preferences, subscriptions and library visibility may have changed during the read.
            var latest = personal.State(identity.Active(user.Id));
            if (!Targets(latest).Any(t => t.Key == target.Key)) continue;
            using var db = store.Open(); using var tx = db.BeginTransaction();
            using var previous = HubStore.Command(db, "SELECT status FROM notification_baselines WHERE user=$0 AND item=$1", U(user), target.Key); previous.Transaction = tx;
            var old = previous.ExecuteScalar() as string;
            using var baseline = HubStore.Command(db, "INSERT INTO notification_baselines VALUES($0,$1,$2) ON CONFLICT(user,item) DO UPDATE SET status=excluded.status", U(user), target.Key, status); baseline.Transaction = tx; baseline.ExecuteNonQuery();
            var p = latest.Preferences;
            if (old is not null && old != status && !p.Muted && (p.JellyfinNotifications || p.DiscordNotifications))
            {
                using var add = HubStore.Command(db, "INSERT INTO media_notifications VALUES($0,$1,$2,$3,$4,0,$5)", HubStore.Nonce(), U(user), target.Key, status, DateTimeOffset.UtcNow.ToString("O"), p.DiscordNotifications ? "pending" : "off"); add.Transaction = tx; add.ExecuteNonQuery();
            }
            using var retention = HubStore.Command(db, "DELETE FROM media_notifications WHERE user=$0 AND (created<$1 OR id NOT IN (SELECT id FROM media_notifications WHERE user=$0 ORDER BY created DESC LIMIT 250))", U(user), DateTimeOffset.UtcNow.AddDays(-30).ToString("O")); retention.Transaction = tx; retention.ExecuteNonQuery(); tx.Commit();
            cursors[user.Id] = target.Key;
        }
    }
    public IReadOnlyList<HubNotification> List(User user)
    {
        var state = personal.State(user); if (state.Preferences.Muted || !state.Preferences.JellyfinNotifications) return [];
        var targets = Targets(state).ToDictionary(t => t.Key);
        var result = new List<HubNotification>();
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT id,item,status,created,seen FROM media_notifications WHERE user=$0 ORDER BY created DESC LIMIT 100", U(user));
        using var r = read.ExecuteReader();
        while (r.Read()) if (targets.TryGetValue(r.GetString(1), out var target)) result.Add(new(r.GetString(0), target.Native, target.Title, r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4) != 0));
        return result;
    }
    public void Seen(User user, string id)
    {
        using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE media_notifications SET seen=1 WHERE id=$0 AND user=$1", id, U(user)); update.ExecuteNonQuery();
    }
    public async Task Deliver(Func<string, HubNotification, CancellationToken, Task> send, CancellationToken cancellation)
    {
        if (!settings.Current.DiscordEnabled) return;
        var pending = new List<(string Id, Guid User, string Key, string Status, DateTimeOffset At)>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT id,user,item,status,created FROM media_notifications WHERE delivery='pending' AND id>$0 ORDER BY id LIMIT 25", deliveryCursor))
        using (var r = read.ExecuteReader()) while (r.Read()) pending.Add((r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), DateTimeOffset.Parse(r.GetString(4))));
        if (pending.Count == 0) deliveryCursor = "";
        foreach (var notice in pending)
        {
            cancellation.ThrowIfCancellationRequested();
            deliveryCursor = notice.Id;
            User user; Target? target; PersonalPreferences preferences; string discord;
            try
            {
                user = await identity.Linked(notice.User); var state = personal.State(user); preferences = state.Preferences;
                target = Targets(state).FirstOrDefault(t => t.Key == notice.Key); discord = store.DiscordFor(user.Id);
                if (!settings.Current.DiscordEnabled) return;
                if (target is null || preferences.Muted || !preferences.DiscordNotifications || notice.At < DateTimeOffset.UtcNow.AddDays(-2) ||
                    await statuses.Read(user, notice.Key, cancellation) != notice.Status)
                { DeliveryState(notice.Id, "skipped"); continue; }
                var verified = await identity.Discord(discord); var live = personal.State(verified);
                if (verified.Id != notice.User || live.Preferences.Muted || !live.Preferences.DiscordNotifications || !Targets(live).Any(t => t.Key == notice.Key)) continue;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { continue; }
            using (var db = store.Open()) using (var claim = HubStore.Command(db, "UPDATE media_notifications SET delivery='sending' WHERE id=$0 AND delivery='pending'", notice.Id)) if (claim.ExecuteNonQuery() != 1) continue;
            var result = "sent";
            try { await send(discord, new(notice.Id, target.Native, target.Title, notice.Status, notice.At, false), cancellation); }
            catch { result = "uncertain"; }
            DeliveryState(notice.Id, result); cancellation.ThrowIfCancellationRequested();
        }
    }
    private void DeliveryState(string id, string state)
    {
        using var db = store.Open(); using var write = HubStore.Command(db, "UPDATE media_notifications SET delivery=$0 WHERE id=$1", state, id); write.ExecuteNonQuery();
    }
}
