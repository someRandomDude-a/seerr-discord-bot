using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record HubNotification(string Id, Guid? Item, string Title, string Status, DateTimeOffset Created, bool Seen);
public sealed record NotificationDiagnostics(DateTimeOffset? LastCompletedScan, int NextOwnRequestPage, bool JellyfinFeed, bool DiscordOptIn, bool Muted, bool OwnRequests, IReadOnlyDictionary<string, long> Deliveries);
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
public sealed class NotificationFeed(HubStore store, PersonalService personal, INotificationStatus statuses, HostIdentity identity, SettingsService settings, MediaCatalog? catalog = null, ExtraMedia? extras = null)
{
    private readonly ConcurrentDictionary<Guid, string> cursors = new();
    private readonly ConcurrentDictionary<Guid, int> requestPages = new();
    private readonly ConcurrentDictionary<Guid, int> extraOffsets = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> completedScans = new();
    private string deliveryCursor = "";
    private sealed record Target(string Key, Guid? Native, string Title);
    private static string U(User user) => user.Id.ToString("N");
    private string RequestKey(MediaCard card) => $"request:{card.RequestId}:{card.Kind}:{card.ExternalId}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(settings.Current.SeerrUrl)))[..16];
    private async Task<Target?> FindTarget(User user, PersonalState state, string key, CancellationToken cancellation)
    {
        if (key.StartsWith("extra-request:", StringComparison.Ordinal))
        {
            if (!state.Preferences.OwnRequestNotifications || extras is null) return null;
            var extra = await extras.OwnRequestStatus(user, key[14..], cancellation); return new(key, extra.NativeId, "Your request · " + extra.Title);
        }
        if (!key.StartsWith("request:", StringComparison.Ordinal)) return Targets(state).FirstOrDefault(t => t.Key == key);
        var parts = key.Split(':');
        if (!state.Preferences.OwnRequestNotifications || catalog is null || parts.Length != 5 || !int.TryParse(parts[1], out var request) || request <= 0) return null;
        var card = await catalog.OwnRequest(user, request, cancellation);
        return RequestKey(card) == key ? new(key, card.NativeId, "Your request · " + card.Title) : null;
    }
    private async Task<string?> CurrentStatus(User user, string key, CancellationToken cancellation) => key.StartsWith("extra-request:", StringComparison.Ordinal) ? extras is null ? null : (await extras.OwnRequestStatus(user, key[14..], cancellation)).Status :
        key.StartsWith("request:", StringComparison.Ordinal) ? catalog is null ? null : (await catalog.OwnRequest(user, int.Parse(key.Split(':')[1]), cancellation)).Status : await statuses.Read(user, key, cancellation);
    private void Observe(User user, Target target, string status, PersonalPreferences p)
    {
        // Only user-facing stages become alerts. Internal service stage names are not notification content.
        if (status is not ("Available" or "Partially available" or "Pending approval" or "Downloading" or "Requested" or "Failed" or "Declined" or "Completed" or "Not in your library")) return;
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var previous = HubStore.Command(db, "SELECT status FROM notification_baselines WHERE user=$0 AND item=$1", U(user), target.Key); previous.Transaction = tx;
        var old = previous.ExecuteScalar() as string;
        using var baseline = HubStore.Command(db, "INSERT INTO notification_baselines VALUES($0,$1,$2) ON CONFLICT(user,item) DO UPDATE SET status=excluded.status", U(user), target.Key, status); baseline.Transaction = tx; baseline.ExecuteNonQuery();
        if (old is not null && old != status && !p.Muted && (p.JellyfinNotifications || p.DiscordNotifications))
        {
            using var add = HubStore.Command(db, "INSERT INTO media_notifications VALUES($0,$1,$2,$3,$4,0,$5)", HubStore.Nonce(), U(user), target.Key, status, DateTimeOffset.UtcNow.ToString("O"), p.DiscordNotifications ? "pending" : "off"); add.Transaction = tx; add.ExecuteNonQuery();
        }
        using var retention = HubStore.Command(db, "DELETE FROM media_notifications WHERE user=$0 AND (created<$1 OR id NOT IN (SELECT id FROM media_notifications WHERE user=$0 ORDER BY created DESC LIMIT 250))", U(user), DateTimeOffset.UtcNow.AddDays(-30).ToString("O")); retention.Transaction = tx; retention.ExecuteNonQuery(); tx.Commit();
    }
    private static IReadOnlyList<Target> Targets(PersonalState state)
    {
        var external = state.ExternalWatchlist ?? [];
        var extras = state.ExtraWatchlist ?? [];
        var mapped = external.SelectMany(w => w.NativeIds ?? (w.NativeId is Guid id ? [id] : Array.Empty<Guid>())).Select(id => (Guid?)id).Concat(extras.Select(w => w.NativeId)).ToHashSet();
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
            Observe(user, target, status, latest.Preferences);
            cursors[user.Id] = target.Key;
        }
        if (catalog is not null && state.Preferences.OwnRequestNotifications && settings.Current.SeerrUrl.Length > 0 && settings.Current.SeerrKeyEncrypted.Length > 0)
        {
            var page = await catalog.Requests(user, false, requestPages.GetValueOrDefault(user.Id, 1), cancellation);
            foreach (var card in page.Items)
            {
                cancellation.ThrowIfCancellationRequested();
                if (card.RequestId is not > 0) throw new HubError("Request notification identity is incomplete.");
                var latest = personal.State(identity.Active(user.Id)); if (!latest.Preferences.OwnRequestNotifications) break;
                Observe(user, new(RequestKey(card), card.NativeId, card.Title), card.Status, latest.Preferences);
            }
            requestPages[user.Id] = page.Page < page.Pages ? page.Page + 1 : 1;
        }
        if (extras is not null && state.Preferences.OwnRequestNotifications)
        {
            var requests = extras.OwnNotificationRequests(user); var offset = extraOffsets.GetValueOrDefault(user.Id); if (offset >= requests.Count) offset = 0;
            foreach (var request in requests.Skip(offset).Take(10))
            {
                var card = await extras.OwnRequestStatus(user, request, cancellation); var latest = personal.State(identity.Active(user.Id)); if (!latest.Preferences.OwnRequestNotifications) break;
                Observe(user, new("extra-request:" + request, card.NativeId, card.Title), card.Status, latest.Preferences);
            }
            extraOffsets[user.Id] = offset + 10 < requests.Count ? offset + 10 : 0;
        }
        completedScans[user.Id] = DateTimeOffset.UtcNow;
    }
    public NotificationDiagnostics Diagnostics(User user)
    {
        var p = personal.State(identity.Active(user.Id)).Preferences; var states = new Dictionary<string, long>();
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT delivery,COUNT(*) FROM media_notifications WHERE user=$0 GROUP BY delivery", U(user)); using var r = read.ExecuteReader();
        while (r.Read()) { var state = r.GetString(0); if (state is not ("pending" or "off" or "sending" or "sent" or "uncertain" or "skipped")) throw new HubError("Invalid retained notification delivery state."); states.Add(state, r.GetInt64(1)); }
        return new(completedScans.TryGetValue(user.Id, out var at) ? at : null, requestPages.GetValueOrDefault(user.Id, 1), p.JellyfinNotifications, p.DiscordNotifications, p.Muted, p.OwnRequestNotifications, states);
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
    public async Task<IReadOnlyList<HubNotification>> ListAsync(User user, CancellationToken cancellation)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(10)); cancellation = budget.Token;
        var state = personal.State(user); var result = List(user).ToList();
        if (catalog is null && extras is null || state.Preferences.Muted || !state.Preferences.JellyfinNotifications || !state.Preferences.OwnRequestNotifications) return result;
        var rows = new List<(string Id, string Key, string Status, DateTimeOffset Created, bool Seen)>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT id,item,status,created,seen FROM media_notifications WHERE user=$0 AND (item LIKE 'request:%' OR item LIKE 'extra-request:%') ORDER BY created DESC LIMIT 20", U(user)))
        using (var r = read.ExecuteReader()) while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), DateTimeOffset.Parse(r.GetString(3)), r.GetInt32(4) == 1));
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested();
            try { if (await FindTarget(user, state, row.Key, cancellation) is { } target) result.Add(new(row.Id, target.Native, target.Title, row.Status, row.Created, row.Seen)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { /* Missing ownership, changed media, restrictions and outages never disclose retained request metadata. */ }
        }
        var latest = personal.State(identity.Active(user.Id));
        if (latest.Preferences.Muted || !latest.Preferences.JellyfinNotifications) return [];
        return List(user).Concat(result.Where(r => rows.Any(row => row.Id == r.Id) && latest.Preferences.OwnRequestNotifications))
            .OrderByDescending(r => r.Created).Take(100).ToArray();
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
                target = await FindTarget(user, state, notice.Key, cancellation); discord = store.DiscordFor(user.Id);
                if (!settings.Current.DiscordEnabled) return;
                if (target is null || preferences.Muted || !preferences.DiscordNotifications || notice.At < DateTimeOffset.UtcNow.AddDays(-2) ||
                    await CurrentStatus(user, notice.Key, cancellation) != notice.Status)
                { DeliveryState(notice.Id, "skipped"); continue; }
                var verified = await identity.Discord(discord); var live = personal.State(verified);
                if (verified.Id != notice.User || live.Preferences.Muted || !live.Preferences.DiscordNotifications || await FindTarget(verified, live, notice.Key, cancellation) is null) continue;
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
