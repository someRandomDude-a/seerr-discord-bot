using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaHub;

public sealed record MediaCard(string Kind, int ExternalId, Guid? NativeId, string Title, string Overview,
    string Status, bool Available, bool Requestable, string? Poster, int? RequestId = null, bool Is4k = false, IReadOnlyList<int>? Seasons = null, ActivityReceipt? Activity = null, bool NativeOnly = false);
public sealed record MediaOpen(string Item, ActivityReceipt Activity);
public sealed record MediaEdition(Guid Id, string Title);
public sealed record MediaPage(IReadOnlyList<MediaCard> Items, int Page, int Pages);
public sealed record UserQuota(int? Days, int? Limit, int? Used, int? Remaining, bool? Restricted);
public sealed record RequestPermissions(bool Movies, bool Series, bool Movie4k, bool Series4k, UserQuota MovieQuota, UserQuota SeriesQuota);

/// <summary>Allowlisted metadata projection shared by Discord and authenticated web clients.</summary>
public sealed class MediaCatalog(SeerrBridge bridge, ILibraryManager library, MediaActivity activity, IExternalMedia? access = null)
{
    private bool Disclosable(User user, string kind, int id) => (access ?? new ExternalMedia(library)).Resolve(user, new(kind, id)).Disclosable;
    internal void RequireAccess(User user, string kind, int id)
    { Kind(kind); if (id <= 0 || !Disclosable(user, kind, id)) throw new HubError("Item unavailable."); }
    internal static void Kind(string kind) { if (kind is not ("movie" or "tv")) throw new HubError("Choose movie or tv."); }
    public IReadOnlyList<BaseItem> Matches(User user, string kind, int id)
    {
        Kind(kind);
        var rows = library.GetItemList(new InternalItemsQuery(user) { Recursive = true, Limit = 51, GroupByPresentationUniqueKey = false, GroupBySeriesPresentationUniqueKey = false,
            IncludeItemTypes = [kind == "movie" ? BaseItemKind.Movie : BaseItemKind.Series], HasAnyProviderId = new Dictionary<string, string> { ["Tmdb"] = id.ToString() } });
        if (rows.Count >= 51) throw new HubError("Edition scan incomplete. Choose the edition in Jellyfin.");
        return rows.Where(item => item.ProviderIds.TryGetValue("Tmdb", out var actual) && actual == id.ToString() && item.IsVisibleStandalone(user)).ToArray();
    }
    public IReadOnlyList<MediaEdition> Editions(User user, string kind, int id)
    { RequireAccess(user, kind, id); return Matches(user, kind, id).OrderBy(i => i.Id).Select(i => new MediaEdition(i.Id, i.Name)).ToArray(); }
    public MediaOpen OpenEdition(User user, string kind, int id, Guid edition)
    {
        if (!Editions(user, kind, id).Any(e => e.Id == edition)) throw new HubError("This edition is not currently accessible for this title.");
        return new(edition.ToString("N"), activity.Record(user, edition, MediaInteraction.Open));
    }
    public MediaPage Library(User user, string kind, string query, int page)
    {
        if (page is < 1 or > 500 || query.Length > 100) throw new HubError("Invalid library query.");
        var types = kind switch { "all" => new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.MusicAlbum, BaseItemKind.Book, BaseItemKind.AudioBook },
            "movie" => [BaseItemKind.Movie], "tv" => [BaseItemKind.Series], "music" => [BaseItemKind.MusicAlbum], "book" => [BaseItemKind.Book, BaseItemKind.AudioBook], _ => throw new HubError("Invalid media type.") };
        var result = library.GetItemsResult(new InternalItemsQuery(user) { Recursive = true, IncludeItemTypes = types,
            SearchTerm = query, StartIndex = (page - 1) * 20, Limit = 20, EnableTotalRecordCount = true });
        var rows = result.Items.Where(item => item.IsVisibleStandalone(user)).Select(item => new MediaCard(
            item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : item is MediaBrowser.Controller.Entities.Audio.MusicAlbum ? "music" : "book",
            item.ProviderIds.TryGetValue("Tmdb", out var id) && int.TryParse(id, out var parsed) ? parsed : 0,
             item.Id, item.Name, item.Overview ?? "", "Available", true, false, null, NativeOnly: true)).ToArray();
        return new(rows, page, Math.Max(1, (int)Math.Ceiling(result.TotalRecordCount / 20d)));
    }
    public MediaCard NativeDetails(User user, Guid id, bool interaction = false)
    {
        var item = library.GetItemById(id); if (item is null || !item.IsVisibleStandalone(user)) throw new HubError("Item unavailable.");
        var kind = item is MediaBrowser.Controller.Entities.TV.Series or MediaBrowser.Controller.Entities.TV.Episode ? "tv" : item is MediaBrowser.Controller.Entities.Book or MediaBrowser.Controller.Entities.AudioBook ? "book" : item is MediaBrowser.Controller.Entities.Audio.MusicAlbum or MediaBrowser.Controller.Entities.Audio.Audio ? "music" : item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : throw new HubError("Unsupported media item.");
        var reference = MediaActivity.Root(item);
        return new(kind, reference.IsVisibleStandalone(user) && reference.ProviderIds.TryGetValue("Tmdb", out var tmdb) && int.TryParse(tmdb, out var external) && external > 0 ? external : 0, id, item.Name, item.Overview ?? "", "Available", true, false, null,
            Activity: interaction ? activity.Record(user, id, MediaInteraction.Details) : null, NativeOnly: true);
    }
    private MediaCard Card(User user, string kind, int id, JsonNode details, JsonNode? request = null)
    {
        Kind(kind); var info = details["mediaInfo"]; var is4k = request?["is4k"]?.GetValue<bool>() ?? false;
        var visible = Matches(user, kind, id); var native = visible.Count == 1 ? visible[0].Id : (Guid?)null;
        if (Guid.TryParse(info?[is4k ? "jellyfinMediaId4k" : "jellyfinMediaId"]?.GetValue<string>(), out var candidate) && visible.Any(item => item.Id == candidate)) native = candidate;
        var availability = info?[is4k ? "status4k" : "status"]?.GetValue<int>();
        var available = visible.Count > 0 && availability is 4 or 5;
        var status = available ? availability == 4 ? "Partially available" : "Available" : request?["status"]?.GetValue<int>() switch
        { 3 => "Declined", 4 => "Failed", _ => availability switch { 2 => "Pending approval", 3 => "Downloading", _ => request?["status"]?.GetValue<int>() switch { 1 => "Pending approval", 2 => "Downloading", 5 => "Completed", _ => "Not in your library" } } };
        var poster = details["posterPath"]?.GetValue<string>();
        if (poster is not null && !System.Text.RegularExpressions.Regex.IsMatch(poster, @"^/[A-Za-z0-9_-]{1,100}\.(jpg|png|webp)$")) poster = null;
        return new(kind, id, native, details["title"]?.GetValue<string>() ?? details["name"]?.GetValue<string>() ?? "Untitled",
            details["overview"]?.GetValue<string>() ?? "", status, available, availability is not (2 or 3 or 4 or 5), poster,
            request?["id"]?.GetValue<int>(), is4k, kind == "tv" ? Seasons(details["seasons"]) : null);
    }
    private static IReadOnlyList<int>? Seasons(JsonNode? value)
    {
        if (value is null) return null; var rows = value.AsArray(); if (rows.Count > 100) throw new HubError("Too many season choices.");
        var seasons = rows.Select(row => row?["seasonNumber"]?.GetValue<int>() ?? -1).ToArray();
        if (seasons.Any(n => n is < 0 or > 10000) || seasons.Distinct().Count() != seasons.Length) throw new HubError("Ambiguous season choices.");
        return seasons.Order().ToArray();
    }
    public async Task<MediaPage> Browse(User user, string kind, string query, int page, CancellationToken cancellation)
    {
        Kind(kind); if (query.Length > 100 || page is < 1 or > 500) throw new HubError("Invalid search.");
        using var scope = await bridge.Open(user, cancellation);
        var path = query.Length > 0 ? "search?query=" + Uri.EscapeDataString(query) + "&page=" + page : "discover/" + (kind == "movie" ? "movies" : "tv") + "?page=" + page;
        var result = await scope.Get(path, cancellation); var cards = new List<MediaCard>();
        if (result["results"]!.AsArray().Count > 100) throw new HubError("Catalog page exceeds its safe size.");
        foreach (var row in result["results"]!.AsArray())
        {
            if (row is null || row["mediaType"]?.GetValue<string>() is string type && type != kind) continue;
            var id = row["id"]!.GetValue<int>();
            if (Disclosable(user, kind, id)) cards.Add(Card(user, kind, id, row));
        }
        await scope.Verify(cancellation);
        return new(cards, page, Math.Clamp(result["totalPages"]?.GetValue<int>() ?? 1, 1, 500));
    }
    public async Task<MediaCard> Details(User user, string kind, int id, CancellationToken cancellation, bool recordInteraction = false)
    {
        Kind(kind); if (id <= 0) throw new HubError("Invalid media ID.");
        if (!Disclosable(user, kind, id)) throw new HubError("Item unavailable.");
        using var scope = await bridge.Open(user, cancellation); var details = await scope.Get(kind + "/" + id, cancellation);
        if (details["id"]?.GetValue<int>() != id) throw new HubError("Upstream media identity changed.");
        await scope.Verify(cancellation);
        if (!Disclosable(user, kind, id)) throw new HubError("Item unavailable.");
        var card = Card(user, kind, id, details);
        if (recordInteraction && card.NativeId is Guid native) card = card with { Activity = activity.Record(user, native, MediaInteraction.Details) };
        return card;
    }
    public async Task<MediaPage> Requests(User user, bool all, int page, CancellationToken cancellation)
    {
        if (page is < 1 or > 500) throw new HubError("Invalid request page.");
        using var scope = await bridge.Open(user, cancellation);
        if (all && !scope.Can(16 | 16384)) throw new HubError("Seerr request-view permission is required.");
        var result = await scope.Get("request?take=20&skip=" + ((page - 1) * 20) + (all ? "" : "&requestedBy=" + scope.Viewer.Id), cancellation);
        var rows = result["results"]!.AsArray(); var cards = new List<MediaCard>();
        if (rows.Count > 20) throw new HubError("Request page exceeds its safe size.");
        foreach (var row in rows)
        {
            if (row is null) throw new HubError("Invalid request response.");
            if (!all && row["requestedBy"]?["id"]?.GetValue<int>() != scope.Viewer.Id) throw new HubError("Seerr returned another user's request. Access blocked.");
            var kind = row["type"]!.GetValue<string>(); Kind(kind); var id = row["media"]!["tmdbId"]!.GetValue<int>();
            if (!Disclosable(user, kind, id)) continue;
            var details = await scope.Get(kind + "/" + id, cancellation);
            if (details["id"]?.GetValue<int>() != id) throw new HubError("Upstream media identity changed.");
            cards.Add(Card(user, kind, id, details, row));
        }
        await scope.Verify(cancellation);
        return new(cards, page, Math.Max(1, (int)Math.Ceiling((result["pageInfo"]?["results"]?.GetValue<int>() ?? cards.Count) / 20d)));
    }
    public async Task<RequestPermissions> Permissions(User user, CancellationToken cancellation)
    {
        using var scope = await bridge.Open(user, cancellation); var quota = await scope.Get("user/" + scope.Viewer.Id + "/quota", cancellation);
        static UserQuota Project(JsonNode? node) => new(node?["days"]?.GetValue<int>(), node?["limit"]?.GetValue<int>(), node?["used"]?.GetValue<int>(), node?["remaining"]?.GetValue<int>(), node?["restricted"]?.GetValue<bool>());
        return new(scope.Can(32 | 262144), scope.Can(32 | 524288), scope.Can(1024 | 2048), scope.Can(1024 | 4096), Project(quota["movie"]), Project(quota["tv"]));
    }
    public async Task<MediaCard> OwnRequest(User user, int request, CancellationToken cancellation)
    {
        if (request <= 0) throw new HubError("Invalid request identity.");
        using var scope = await bridge.Open(user, cancellation); var row = await scope.Get("request/" + request, cancellation);
        if (row["id"]?.GetValue<int>() != request || row["requestedBy"]?["id"]?.GetValue<int>() != scope.Viewer.Id) throw new HubError("Your request could not be verified.");
        var kind = row["type"]?.GetValue<string>() ?? ""; var id = row["media"]?["tmdbId"]?.GetValue<int>() ?? 0; RequireAccess(user, kind, id);
        var details = await scope.Get(kind + "/" + id, cancellation);
        if (details["id"]?.GetValue<int>() != id) throw new HubError("Upstream media identity changed.");
        await scope.Verify(cancellation); RequireAccess(user, kind, id); return Card(user, kind, id, details, row);
    }
    public async Task<MediaOpen> Open(User user, string kind, int id, CancellationToken cancellation)
    {
        var card = await Details(user, kind, id, cancellation, false);
        if (card.NativeId is not Guid native) throw new HubError("No unique accessible item. Choose an edition in Jellyfin.");
        return new(native.ToString("N"), activity.Record(user, native, MediaInteraction.Open)); // Same-server ID, never an upstream URL.
    }
}
