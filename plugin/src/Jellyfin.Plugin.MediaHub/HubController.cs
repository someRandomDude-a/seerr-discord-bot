using MediaBrowser.Common.Api;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController]
[Authorize]
[Route("MediaHub")]
[RequestSizeLimit(65536), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class HubController(IServiceProvider provider) : ControllerBase
{
    // Resolve state only inside this plugin's error boundary, never during host startup.
    private HostIdentity identity => provider.GetRequiredService<HostIdentity>();
    private HubStore store => provider.GetRequiredService<HubStore>();
    private SettingsService settings => provider.GetRequiredService<SettingsService>();
    private WatchPartyIntegration watchParty => provider.GetRequiredService<WatchPartyIntegration>();
    private PersonalService personal => provider.GetRequiredService<PersonalService>();
    private DeletionQueue deletions => provider.GetRequiredService<DeletionQueue>();
    private MediaCatalog catalog => provider.GetRequiredService<MediaCatalog>();
    private MediaRequests requests => provider.GetRequiredService<MediaRequests>();
    private ExtraMedia extras => provider.GetRequiredService<ExtraMedia>();
    private async Task<Jellyfin.Database.Implementations.Entities.User> Viewer() => identity.Active((await identity.Browser(User)).UserId);
    private async Task Administrator()
    { if (!(await Viewer()).HasPermission(PermissionKind.IsAdministrator)) throw new HubError("Jellyfin administrator access is required."); }
    [HttpGet("Storage")]
    public Task<IActionResult> Storage(CancellationToken cancellation) => Safe(async () =>
    {
        using var scope = await provider.GetRequiredService<SeerrBridge>().Open(await Viewer(), cancellation);
        var result = await provider.GetRequiredService<ArrRegistry>().Storage(cancellation); await scope.Verify(cancellation); return result;
    });
    [HttpGet("SeerrLink")]
    public Task<IActionResult> SeerrLink(CancellationToken cancellation) => Safe(async () =>
    { using var scope = await provider.GetRequiredService<SeerrBridge>().Open(await Viewer(), cancellation); return new { Url = scope.Viewer.Origin }; });
    [HttpGet("Extras/{kind}/Search")]
    public Task<IActionResult> ExtraSearch(string kind, CancellationToken cancellation, string query = "", int page = 1) => Safe(async () => await extras.Search(await Viewer(), kind, query, page, cancellation));
    [HttpGet("Extras/{kind}/{foreign}/Details")]
    public Task<IActionResult> ExtraDetails(string kind, string foreign, CancellationToken cancellation) => Safe(async () => await extras.Details(await Viewer(), kind, foreign, false, cancellation));
    [HttpPost("Extras/{kind}/{foreign}/Details")]
    public Task<IActionResult> ExtraInteract(string kind, string foreign, CancellationToken cancellation) => Safe(async () => await extras.Details(await Viewer(), kind, foreign, true, cancellation));
    public sealed record ExtraSelection(string Kind, string ForeignId, string? Edition = null);
    [HttpPost("Extras/Requests/Prepare")]
    public Task<IActionResult> ExtraPrepare(ExtraSelection selection, CancellationToken cancellation) => Safe(async () => await extras.Prepare(await Viewer(), selection.Kind, selection.ForeignId, selection.Edition, cancellation));
    [HttpPost("Extras/Requests/{id}/Confirm")]
    public Task<IActionResult> ExtraConfirm(string id, CancellationToken cancellation) => Safe(async () => await extras.Confirm(await Viewer(), id, async () => { await identity.Browser(User); }, cancellation));
    [HttpPost("Extras/Requests/{id}/Cancel")]
    public Task<IActionResult> ExtraCancel(string id) => Safe(async () => extras.Cancel(await Viewer(), id));
    [HttpGet("Extras/Requests/{id}/Receipt")]
    public Task<IActionResult> ExtraReceipt(string id) => Safe(async () => extras.Receipt(await Viewer(), id));
    [HttpPost("Me/ExtraWatchlist/{kind}/{foreign}")]
    public Task<IActionResult> ExtraFollow(string kind, string foreign, ExternalWatchUpdate update, CancellationToken cancellation) => Safe(async () =>
    { var viewer = await Viewer(); var recorded = await extras.Follow(viewer, kind, foreign, update.Subscribed, update.Revision, async () => { await identity.Browser(User); }, cancellation, update.SaveOnly); return personal.State(viewer) with { Activity = recorded }; });
    [HttpGet("Catalog/Library")]
    public Task<IActionResult> Library(string kind = "all", string query = "", int page = 1) => Safe(async () => catalog.Library(await Viewer(), kind, query, page));
    [HttpGet("Catalog/Browse")]
    public Task<IActionResult> Browse(CancellationToken cancellation, string kind = "movie", string query = "", int page = 1) => Safe(async () => await catalog.Browse(await Viewer(), kind, query, page, cancellation));
    [HttpGet("Catalog/{kind}/{id:int}")]
    public Task<IActionResult> Details(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Details(await Viewer(), kind, id, cancellation));
    [HttpGet("Catalog/{kind}/{id:int}/Poster")]
    public Task<IActionResult> Poster(string kind, int id, CancellationToken cancellation) => Safe(async () =>
    {
        var user = await Viewer(); var card = await catalog.Details(user, kind, id, cancellation);
        var image = await provider.GetRequiredService<PosterAssets>().Download(card.Poster ?? "", cancellation);
        var current = await catalog.Details(await Viewer(), kind, id, cancellation);
        if (current.Poster != card.Poster) throw new HubError("Artwork changed. Load it again explicitly.");
        Response.Headers["X-Content-Type-Options"] = "nosniff"; Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'"; return File(image.Bytes, image.Mime);
    });
    [HttpPost("Catalog/{kind}/{id:int}/Details")]
    public Task<IActionResult> InteractDetails(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Details(await Viewer(), kind, id, cancellation, true));
    [HttpPost("Catalog/{kind}/{id:int}/Open")]
    public Task<IActionResult> OpenMedia(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Open(await Viewer(), kind, id, cancellation));
    [HttpGet("Catalog/{kind}/{id:int}/Editions")]
    public Task<IActionResult> Editions(string kind, int id) => Safe(async () => catalog.Editions(await Viewer(), kind, id));
    [HttpPost("Catalog/{kind}/{id:int}/Editions/{edition:guid}/Open")]
    public Task<IActionResult> OpenEdition(string kind, int id, Guid edition) => Safe(async () => catalog.OpenEdition(await Viewer(), kind, id, edition));
    [HttpGet("Requests")]
    public Task<IActionResult> Requests(CancellationToken cancellation, bool all = false, int page = 1) => Safe(async () => await catalog.Requests(await Viewer(), all, page, cancellation));
    [HttpGet("Me/RequestPermissions")]
    public Task<IActionResult> RequestPermissions(CancellationToken cancellation) => Safe(async () => await catalog.Permissions(await Viewer(), cancellation));
    public sealed record RequestSelection(string Kind, int ExternalId, bool Is4k = false, int[]? Seasons = null);
    [HttpPost("Requests/Prepare")]
    public Task<IActionResult> PrepareRequest(RequestSelection selection, CancellationToken cancellation) => Safe(async () =>
        await requests.Prepare(await Viewer(), selection.Kind, selection.ExternalId, selection.Is4k, selection.Seasons, cancellation));
    [HttpPost("Requests/{id}/Confirm")]
    public Task<IActionResult> ConfirmRequest(string id, CancellationToken cancellation) => Safe(async () =>
        await requests.Confirm(await Viewer(), id, async () => { await identity.Browser(User); }, cancellation));
    [HttpGet("Requests/{id}/Receipt")]
    public Task<IActionResult> RequestReceipt(string id) => Safe(async () => requests.Receipt(await Viewer(), id));
    [HttpPost("Requests/{id}/Cancel")]
    public Task<IActionResult> CancelRequestPlan(string id) => Safe(async () => requests.Cancel(await Viewer(), id));
    public sealed record Interaction(MediaInteraction Action);
    [HttpPost("Items/{item:guid}/Activity")]
    public Task<IActionResult> Activity(Guid item, Interaction update) => Safe(async () =>
        provider.GetRequiredService<MediaActivity>().Record(identity.Active((await identity.Browser(User)).UserId), item, update.Action));
    [HttpGet("Items/{item:guid}/DeletionRequests")]
    public Task<IActionResult> ItemDeletionRequests(Guid item, CancellationToken cancellation) => Safe(async () =>
        await provider.GetRequiredService<IRequestAuthority>().FindOwned(identity.Active((await identity.Browser(User)).UserId), item, cancellation));
    [HttpGet("Items/{item:guid}/Controls")]
    public Task<IActionResult> ItemControls(Guid item) => Safe(async () => provider.GetRequiredService<IViewerMedia>().Find(await Viewer(), item) ?? throw new HubError("Item unavailable."));
    [HttpPost("Items/{item:guid}/Details")]
    public Task<IActionResult> NativeDetails(Guid item) => Safe(async () => catalog.NativeDetails(await Viewer(), item, true));
    [HttpGet("Deletions")]
    public Task<IActionResult> Deletions() => Safe(async () => deletions.List(identity.Active((await identity.Browser(User)).UserId)));
    [HttpGet("Deletions/{id}/Audit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> DeletionAudit(string id) => Safe(async () => deletions.Audit(identity.Active((await identity.Browser(User)).UserId), id));
    public sealed record Proposal(int RequestId, Guid Item);
    [HttpPost("Deletions")]
    public Task<IActionResult> ProposeDeletion(Proposal proposal, CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = identity.Active((await identity.Browser(User)).UserId);
        await deletions.Propose(viewer, proposal.RequestId, proposal.Item, cancellation); return new { Saved = true };
    });
    [HttpPost("Deletions/Refresh")]
    public Task<IActionResult> RefreshDeletions(CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = identity.Active((await identity.Browser(User)).UserId);
        await deletions.Refresh(viewer, cancellation); return deletions.List(viewer);
    });
    [HttpPost("Deletions/{id}/Undo")]
    public Task<IActionResult> UndoDeletion(string id) => Safe(async () =>
    { deletions.Undo(identity.Active((await identity.Browser(User)).UserId), id); return new { Saved = true }; });
    [HttpGet("Me/Consent")]
    public Task<IActionResult> ConsentNotices() => Safe(async () => deletions.Notices(identity.Active((await identity.Browser(User)).UserId)));
    public sealed record ConsentResponse(bool Approve);
    [HttpPost("Me/Consent/{id}")]
    public Task<IActionResult> RespondConsent(string id, ConsentResponse response, CancellationToken cancellation) => Safe(async () =>
    {
        await deletions.Respond(identity.Active((await identity.Browser(User)).UserId), id, response.Approve, cancellation); return new { Saved = true };
    });
    private async Task<IActionResult> Safe(Func<Task<object>> operation)
    {
        Response.Headers["Cache-Control"] = "no-store";
        try { var result = await operation(); return result is IActionResult response ? response : Ok(result); }
        catch (HubError error) when (error.Code == WatchPartyIntegration.UnavailableCode) { return StatusCode(503, new { Error = error.Message, error.Code }); }
        catch (HubError error) { return BadRequest(new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Action not confirmed. Check the result before retrying." }); }
    }
    [HttpGet("Status")]
    public Task<IActionResult> Status() => Safe(async () =>
    {
        var browser = await identity.Browser(User);
        return new { Linked = store.IsLinked(browser.UserId), WatchParty = watchParty.Status(), Party = watchParty.Status(), Web = provider.GetRequiredService<WebIntegration>().Status(), FeatureParityComplete = false };
    });
    public sealed record Approval(string Code, string DiscordId);
    [HttpPost("Link/Approve")]
    public Task<IActionResult> Approve(Approval approval) => Safe(async () =>
    {
        var browser = await identity.Browser(User);
        identity.Approve(User, browser, approval.Code, approval.DiscordId);
        return new { Linked = true };
    });
    public sealed record CreateParty(string Name);
    // Compatibility tombstones: old clients receive an explicit unavailable result, not
    // a group operation or consumption/migration of legacy invitations and tickets.
    [HttpPost("Parties")]
    public Task<IActionResult> Create(CreateParty request, CancellationToken cancellation) => WatchPartyUnavailable();
    public sealed record JoinParty(string Ticket);
    [HttpPost("Parties/Join")]
    public Task<IActionResult> Join(JoinParty request, CancellationToken cancellation) => WatchPartyUnavailable();
    [HttpPost("Parties/{id}/Close")]
    public Task<IActionResult> Close(string id) => WatchPartyUnavailable();
    private Task<IActionResult> WatchPartyUnavailable() => Safe(async () =>
    {
        await identity.Browser(User);
        throw watchParty.Unavailable();
    });
    [HttpGet("Me")]
    public Task<IActionResult> Me() => Safe(async () => personal.State(identity.Active((await identity.Browser(User)).UserId)));
    [HttpGet("Me/Notifications")]
    public Task<IActionResult> Notifications(CancellationToken cancellation) => Safe(async () => await provider.GetRequiredService<NotificationFeed>().ListAsync(await Viewer(), cancellation));
    [HttpGet("Me/Notifications/Diagnostics")]
    public Task<IActionResult> NotificationDiagnostics() => Safe(async () => provider.GetRequiredService<NotificationFeed>().Diagnostics(await Viewer()));
    [HttpPost("Me/Notifications/{id}/Seen")]
    public Task<IActionResult> SeenNotification(string id) => Safe(async () => { provider.GetRequiredService<NotificationFeed>().Seen(await Viewer(), id); return new { Saved = true }; });
    public sealed record ExternalWatchUpdate(long Revision, bool? Subscribed, bool SaveOnly = false);
    [HttpPost("Me/Watchlist/{kind}/{id:int}")]
    public Task<IActionResult> ExternalWatch(string kind, int id, ExternalWatchUpdate update, CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = await Viewer();
        var recorded = await provider.GetRequiredService<WatchlistSync>().Follow(viewer, new(kind, id), update.Subscribed, update.Revision, async () => { await identity.Browser(User); }, cancellation, update.SaveOnly);
        return personal.State(viewer) with { Activity = recorded };
    });
    [HttpPost("Me/Watchlist/Sync")]
    public Task<IActionResult> SyncWatchlist(CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = await Viewer();
        return await provider.GetRequiredService<WatchlistSync>().Sync(viewer, async () => { await identity.Browser(User); }, cancellation);
    });
    [HttpGet("Me/Watchlist/Sync/Issues")]
    public Task<IActionResult> SyncIssues() => Safe(async () => provider.GetRequiredService<WatchlistSync>().Issues(await Viewer()));
    [HttpPost("Me/Watchlist/Sync/Review")]
    public Task<IActionResult> ReviewSync(MediaKey key, CancellationToken cancellation) => Safe(async () => await provider.GetRequiredService<WatchlistSync>().Review(await Viewer(), key, async () => { await identity.Browser(User); }, cancellation));
    public sealed record SyncAcceptance(string Token);
    [HttpPost("Me/Watchlist/Legacy/Review")]
    public Task<IActionResult> ReviewLegacy(CancellationToken cancellation) => Safe(async () => await provider.GetRequiredService<WatchlistSync>().ReviewLegacy(await Viewer(), async () => { await identity.Browser(User); }, cancellation));
    [HttpPost("Me/Watchlist/Legacy/Accept")]
    public Task<IActionResult> AcceptLegacy(SyncAcceptance proof, CancellationToken cancellation) => Safe(async () => { await provider.GetRequiredService<WatchlistSync>().AcceptLegacy(await Viewer(), proof.Token, async () => { await identity.Browser(User); }, cancellation); return new { Saved = true }; });
    [HttpPost("Me/Watchlist/Sync/AcceptRemote")]
    public Task<IActionResult> AcceptRemoteSync(SyncAcceptance proof, CancellationToken cancellation) => Safe(async () => await provider.GetRequiredService<WatchlistSync>().AcceptRemote(await Viewer(), proof.Token, async () => { await identity.Browser(User); }, cancellation));
    public sealed record PersonalUpdate(long Revision, PersonalPreferences Preferences);
    [HttpPost("Me")]
    public Task<IActionResult> SavePersonal(PersonalUpdate update) => Safe(async () =>
        personal.Save(identity.Active((await identity.Browser(User)).UserId), update.Revision, update.Preferences));
    public sealed record BellUpdate(long Revision, bool Subscribed);
    [HttpPost("Me/Watchlist/{item:guid}/Bell")]
    public Task<IActionResult> Bell(Guid item, BellUpdate update) => Safe(async () =>
        personal.Bell(identity.Active((await identity.Browser(User)).UserId), item, update.Subscribed, update.Revision));
    public sealed record WatchlistRemove(long Revision);
    [HttpPost("Me/Watchlist/{item:guid}/Add")]
    public Task<IActionResult> Add(Guid item, WatchlistRemove update) => Safe(async () =>
        personal.Add(identity.Active((await identity.Browser(User)).UserId), item, update.Revision));
    [HttpPost("Me/Watchlist/{item:guid}/Remove")]
    public Task<IActionResult> Remove(Guid item, WatchlistRemove update) => Safe(async () =>
        personal.Remove(identity.Active((await identity.Browser(User)).UserId), item, update.Revision));
    [HttpGet("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Settings() => Safe(async () => { await Administrator(); return settings.Public(); });
    [HttpPost("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Save(SettingsUpdate update) => Safe(async () => { await Administrator(); settings.Update(update); return settings.Public(); });
}
