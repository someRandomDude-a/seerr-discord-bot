using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MediaHub;

public enum MediaInteraction { Details, Open, Request, Follow }
public sealed record ActivityReceipt(bool JellyfinUpdated, bool LocalRecorded = true);
public interface IRecentActivity
{
    IReadOnlyList<RecentViewer> Read(Guid item);
}

/// <summary>Explicit item actions only. Never fabricates playback/completion events.</summary>
public sealed class MediaActivity(HubStore store, ILibraryManager library, IUserManager users, IUserDataManager data) : IRecentActivity
{
    internal static IReadOnlyList<BaseItem> Related(ILibraryManager library, BaseItem root)
    {
        if (root is not (Series or MediaBrowser.Controller.Entities.Movies.Movie) || root.ProviderIds is null || !root.ProviderIds.TryGetValue("Tmdb", out var id)) return [root];
        var rows = library.GetItemList(new InternalItemsQuery { Recursive = true, Limit = 1001, GroupByPresentationUniqueKey = false, GroupBySeriesPresentationUniqueKey = false,
            IncludeItemTypes = [root is Series ? BaseItemKind.Series : BaseItemKind.Movie], HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = id } });
        if (rows.Count >= 1001) throw new HubError("Edition scan incomplete.");
        return rows.Append(root).DistinctBy(item => item.Id).ToArray();
    }
    public static BaseItem Root(BaseItem item)
    {
        if (item is Series) return item;
        var seen = new HashSet<Guid> { item.Id }; var count = 0;
        foreach (var parent in item.GetParents())
        {
            if (++count > 64 || parent is null || !seen.Add(parent.Id)) throw new HubError("Invalid or excessively deep library ancestry.");
            if (parent is Series) return parent;
        }
        return item;
    }
    public ActivityReceipt Record(User viewer, Guid itemId, MediaInteraction interaction)
    {
        if (!Enum.IsDefined(interaction)) throw new HubError("Unsupported media action.");
        if (viewer.HasPermission(PermissionKind.IsDisabled)) throw new HubError("Account unavailable.");
        var item = library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(viewer)) throw new HubError("Item unavailable.");
        var now = DateTimeOffset.UtcNow; var root = Root(item);
        // Journal protection first. A failed native update must not erase recent interest.
        using (var db = store.Open())
        using (var write = HubStore.Command(db, "INSERT INTO activity VALUES($0,$1,$2) ON CONFLICT(item,user) DO UPDATE SET at=MAX(at,excluded.at)",
            root.Id.ToString("N"), viewer.Id.ToString("N"), now.ToString("O"))) write.ExecuteNonQuery();
        try
        {
            var previous = data.GetUserData(viewer, item)?.LastPlayedDate;
            if (previous is null || previous.Value.ToUniversalTime() < now.UtcDateTime)
                data.SaveUserData(viewer, item, new UpdateUserItemDataDto { LastPlayedDate = now.UtcDateTime }, UserDataSaveReason.UpdateUserData);
            return new(true);
        }
        catch { return new(false); } // No automatic replay of an uncertain host write.
    }
    public IReadOnlyList<RecentViewer> Read(Guid itemId)
    {
        var root = library.GetItemById(itemId) ?? throw new HubError("Item unavailable.");
        if (Root(root).Id != root.Id) throw new HubError("Use the show's deletion target.");
        var roots = Related(library, root);
        var items = roots.ToList();
        foreach (var series in roots.OfType<Series>())
        {
            var children = library.GetItemList(new InternalItemsQuery { ParentId = series.Id, Recursive = true, Limit = 10001, GroupByPresentationUniqueKey = false, GroupBySeriesPresentationUniqueKey = false });
            if (children.Count >= 10001 || items.Count + children.Count > 10000) throw new HubError("Activity scan incomplete. Deletion is blocked.");
            items.AddRange(children);
        }
        var latest = new Dictionary<Guid, DateTimeOffset>();
        var accounts = users.GetUsers().Take(1001).ToArray();
        if (accounts.Length > 1000 || (long)accounts.Length * items.Count > 250000) throw new HubError("Activity scan exceeds its safe budget. Deletion is blocked.");
        var budget = System.Diagnostics.Stopwatch.StartNew();
        foreach (var user in accounts)
        {
            if (budget.Elapsed > TimeSpan.FromSeconds(5)) throw new HubError("Activity scan exceeded its time budget. Deletion is blocked.");
            // Do not filter out disabled/restricted viewers: their recent activity still protects media.
            var rows = data.GetUserDataBatch(items, user) ?? throw new HubError("Activity scan unavailable.");
            if (items.Any(item => !rows.ContainsKey(item.Id))) throw new HubError("Activity scan incomplete.");
            foreach (var row in rows.Values)
                if (row.LastPlayedDate is DateTime at)
                {
                    var utc = new DateTimeOffset(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc));
                    if (!latest.TryGetValue(user.Id, out var old) || utc > old) latest[user.Id] = utc;
                }
        }
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT user,at FROM activity WHERE item IN (" + string.Join(',', roots.Select((_, i) => "$" + i)) + ")", roots.Select(item => (object?)item.Id.ToString("N")).ToArray());
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var user = Guid.Parse(reader.GetString(0)); var at = DateTimeOffset.Parse(reader.GetString(1));
            if (!latest.TryGetValue(user, out var old) || at > old) latest[user] = at;
        }
        return latest.Select(pair => new RecentViewer(pair.Key, pair.Value)).ToArray();
    }
}
