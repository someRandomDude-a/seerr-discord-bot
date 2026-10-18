using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ActivityTheme(string Background, string Text, string Accent, string Surface);
public sealed record PersonalPreferences(bool JellyfinNotifications = true, bool DiscordNotifications = false, bool Muted = false, int? DeletionDelayDays = null, ActivityTheme? Theme = null, bool OwnRequestNotifications = true);
public sealed record VisibleItem(Guid Id, string Title, string Kind);
public sealed record FollowedItem(Guid ItemId, string Title, string Kind, bool Subscribed);
public sealed record PersonalState(long Revision, PersonalPreferences Preferences, IReadOnlyList<FollowedItem> Watchlist,
    DeletionRules DeletionRules, int EffectiveDeletionDelayDays, IReadOnlyList<ExternalFollow>? ExternalWatchlist = null, ActivityReceipt? Activity = null, IReadOnlyList<ExtraWatch>? ExtraWatchlist = null, bool WatchlistReviewRequired = false);

public interface IViewerMedia
{
    VisibleItem? Find(User viewer, Guid id);
    VisibleItem? FindForConsent(User viewer, Guid protectedItem) => Find(viewer, protectedItem);
}

public sealed class JellyfinViewerMedia(ILibraryManager library) : IViewerMedia
{
    public VisibleItem? FindForConsent(User viewer, Guid protectedItem)
    {
        var root = library.GetItemById(protectedItem); if (root is null) return null;
        foreach (var related in MediaActivity.Related(library, root))
            if (related.IsVisibleStandalone(viewer)) return new(related.Id, related.Name, related.GetType().Name);
        return null;
    }
    public VisibleItem? Find(User viewer, Guid id)
    {
        var item = library.GetItemById(id);
        if (item is null || !item.IsVisibleStandalone(viewer)) return null;
        return new(id, item.Name, item.GetType().Name);
    }
}

/// <summary>Per-user watchlist/subscription state; never changes Jellyfin favorites.</summary>
public sealed class PersonalService(HubStore store, IViewerMedia media, SettingsService? settings = null, MediaActivity? activity = null, WatchlistSync? sync = null, ExtraMedia? extras = null)
{
    private StateIntegrity Protection => (settings ?? new SettingsService(store)).Protection;
    private DeletionRules Rules()
    {
        var config = settings?.Current;
        return config is null ? new() : new(config.RecentActivityDays, config.DeletionDelayDays);
    }
    private static string Key(User viewer) => viewer.Id.ToString("N");
    public PersonalState State(User viewer)
    {
        _ = Protection;
        using var db = store.Open();
        using var transaction = db.BeginTransaction(deferred: true);
        Protection.Check(db, transaction, "watches");
        using var read = HubStore.Command(db, "SELECT revision,preferences FROM personal WHERE user=$0", Key(viewer));
        read.Transaction = transaction;
        long revision = 0; var preferences = new PersonalPreferences();
        using (var reader = read.ExecuteReader())
            if (reader.Read()) { revision = reader.GetInt64(0); preferences = SafeJson.Read<PersonalPreferences>(reader.GetString(1), 4096); }
        Validate(preferences);
        using var watches = HubStore.Command(db, "SELECT item,subscribed FROM subscriptions WHERE user=$0 ORDER BY created DESC,item LIMIT 201", Key(viewer));
        watches.Transaction = transaction;
        var rows = new List<(Guid Id, bool Subscribed)>();
        using (var reader = watches.ExecuteReader())
            while (reader.Read()) rows.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1) != 0));
        transaction.Commit(); // Do not hold the database transaction across host library reads.
        if (rows.Count > 200) throw new HubError("Retained watchlist exceeds its safe size. No data was reset.");
        // Recheck current native visibility on every read, not only when followed.
        var items = new List<FollowedItem>();
        foreach (var row in rows)
        {
            var item = media.Find(viewer, row.Id);
            if (item is not null) items.Add(new(item.Id, item.Title, item.Kind, row.Subscribed));
        }
        var rules = Rules(); rules.Validate();
        // Admin reductions constrain future proposals, not already-persisted queue deadlines.
        var delay = Math.Clamp(preferences.DeletionDelayDays ?? rules.DefaultDelayDays, rules.DefaultDelayDays, rules.RecentActivityDays);
        return new(revision, preferences, items, rules, delay, sync?.List(viewer) ?? [], ExtraWatchlist: extras?.Watches(viewer) ?? [], WatchlistReviewRequired: Protection.NeedsWatchReview(Key(viewer)));
    }
    public PersonalState Save(User viewer, long expectedRevision, PersonalPreferences preferences)
    {
        _ = Protection;
        using var db = store.Open(); using var transaction = db.BeginTransaction();
        Protection.Check(db, transaction, "watches");
        Validate(preferences);
        _ = Rules().Delay(preferences.DeletionDelayDays);
        Ensure(db, transaction, viewer);
        using var update = HubStore.Command(db, "UPDATE personal SET preferences=$0,revision=revision+1 WHERE user=$1 AND revision=$2",
            JsonSerializer.Serialize(preferences), Key(viewer), expectedRevision);
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1) throw new HubError("Your settings changed in another window. Refresh before saving.");
        Protection.Seal(db, transaction, "watches"); transaction.Commit(); return State(viewer);
    }
    internal static void Validate(PersonalPreferences preferences)
    {
        if (preferences is null || preferences.DeletionDelayDays is < 1 or > 3650) throw new HubError("Invalid personal preferences.");
        if (preferences.Theme is { } theme && new[] { theme.Background, theme.Text, theme.Accent, theme.Surface }.Any(value => value is null || !System.Text.RegularExpressions.Regex.IsMatch(value, "\\A#[0-9a-fA-F]{6}\\z"))) throw new HubError("Activity appearance accepts only host-derived color values, not arbitrary CSS.");
    }
    private static void Ensure(SqliteConnection db, SqliteTransaction transaction, User viewer)
    {
        using var command = HubStore.Command(db, "INSERT OR IGNORE INTO personal VALUES($0,0,$1)", Key(viewer), JsonSerializer.Serialize(new PersonalPreferences()));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
    public PersonalState Add(User viewer, Guid itemId, long expectedRevision) => Bell(viewer, itemId, false, expectedRevision, saveOnly: true);
    public PersonalState Bell(User viewer, Guid itemId, bool subscribed, long expectedRevision, bool saveOnly = false)
    {
        _ = Protection;
        if (media.Find(viewer, itemId) is null) throw new HubError("This item is not accessible to your Jellyfin account.");
        var external = sync?.Identify(viewer, itemId);
        var aliases = external is { } mapped ? sync?.NativeAliases(viewer, mapped.Key) : null;
        using var db = store.Open(); using var transaction = db.BeginTransaction(); Protection.Check(db, transaction, "watches"); Ensure(db, transaction, viewer);
        using var revision = HubStore.Command(db, "UPDATE personal SET revision=revision+1 WHERE user=$0 AND revision=$1", Key(viewer), expectedRevision);
        revision.Transaction = transaction;
        if (revision.ExecuteNonQuery() != 1) throw new HubError("Your watchlist changed in another window. Refresh before retrying.");
        if (subscribed || saveOnly)
        {
            using var count = HubStore.Command(db, "SELECT COUNT(*) FROM subscriptions WHERE user=$0 AND item<>$1", Key(viewer), itemId.ToString("N"));
            count.Transaction = transaction;
            if ((long)count.ExecuteScalar()! >= 200) throw new HubError("Your watchlist is full (200 items). Remove an item first.");
            using var update = HubStore.Command(db, """
                INSERT INTO subscriptions VALUES($0,$1,$3,$2) ON CONFLICT(user,item) DO UPDATE SET subscribed=CASE WHEN $4=1 THEN subscriptions.subscribed ELSE excluded.subscribed END
                """, Key(viewer), itemId.ToString("N"), HubStore.Now, subscribed ? 1 : 0, saveOnly ? 1 : 0);
            update.Transaction = transaction;
            update.ExecuteNonQuery();
        }
        else
        {
            using var update = HubStore.Command(db, "UPDATE subscriptions SET subscribed=0 WHERE user=$0 AND item=$1", Key(viewer), itemId.ToString("N"));
            update.Transaction = transaction;
            update.ExecuteNonQuery(); // Unsubscribing does not erase a saved title.
        }
        sync?.NativeIntent(db, transaction, viewer, itemId, external, subscribed, aliases, saveOnly);
        if (!subscribed && !saveOnly) WatchlistSync.ClearAlerts(db, transaction, viewer, itemId.ToString("N"));
        Protection.Seal(db, transaction, "watches"); transaction.Commit();
        var recorded = subscribed || saveOnly ? activity?.Record(viewer, itemId, MediaInteraction.Follow) : null;
        return State(viewer) with { Activity = recorded };
    }
    public PersonalState Remove(User viewer, Guid itemId, long expectedRevision)
    {
        _ = Protection;
        var external = sync?.Identify(viewer, itemId);
        var aliases = external is { } mapped ? sync?.NativeAliases(viewer, mapped.Key) : null;
        using var db = store.Open(); using var transaction = db.BeginTransaction(); Protection.Check(db, transaction, "watches"); Ensure(db, transaction, viewer);
        using var revision = HubStore.Command(db, "UPDATE personal SET revision=revision+1 WHERE user=$0 AND revision=$1", Key(viewer), expectedRevision);
        revision.Transaction = transaction;
        if (revision.ExecuteNonQuery() != 1) throw new HubError("Your watchlist changed in another window. Refresh before retrying.");
        using var remove = HubStore.Command(db, "DELETE FROM subscriptions WHERE user=$0 AND item=$1", Key(viewer), itemId.ToString("N"));
        remove.Transaction = transaction; remove.ExecuteNonQuery();
        sync?.RemoveNative(db, transaction, viewer, itemId, external, aliases ?? []);
        WatchlistSync.ClearAlerts(db, transaction, viewer, itemId.ToString("N"));
        Protection.Seal(db, transaction, "watches"); transaction.Commit(); return State(viewer);
    }
}
