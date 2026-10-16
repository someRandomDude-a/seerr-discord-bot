using MediaBrowser.Common.Api;
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
    private PartyService parties => provider.GetRequiredService<PartyService>();
    private PersonalService personal => provider.GetRequiredService<PersonalService>();
    private DeletionQueue deletions => provider.GetRequiredService<DeletionQueue>();
    private MediaCatalog catalog => provider.GetRequiredService<MediaCatalog>();
    private MediaRequests requests => provider.GetRequiredService<MediaRequests>();
    private ExtraMedia extras => provider.GetRequiredService<ExtraMedia>();
    private async Task<Jellyfin.Database.Implementations.Entities.User> Viewer() => identity.Active((await identity.Browser(User)).UserId);
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
    { var viewer = await Viewer(); var recorded = await extras.Follow(viewer, kind, foreign, update.Subscribed, update.Revision, async () => { await identity.Browser(User); }, cancellation); return personal.State(viewer) with { Activity = recorded }; });
    [HttpGet("Catalog/Library")]
    public Task<IActionResult> Library(string kind = "all", string query = "", int page = 1) => Safe(async () => catalog.Library(await Viewer(), kind, query, page));
    [HttpGet("Catalog/Browse")]
    public Task<IActionResult> Browse(CancellationToken cancellation, string kind = "movie", string query = "", int page = 1) => Safe(async () => await catalog.Browse(await Viewer(), kind, query, page, cancellation));
    [HttpGet("Catalog/{kind}/{id:int}")]
    public Task<IActionResult> Details(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Details(await Viewer(), kind, id, cancellation));
    [HttpPost("Catalog/{kind}/{id:int}/Details")]
    public Task<IActionResult> InteractDetails(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Details(await Viewer(), kind, id, cancellation, true));
    [HttpPost("Catalog/{kind}/{id:int}/Open")]
    public Task<IActionResult> OpenMedia(string kind, int id, CancellationToken cancellation) => Safe(async () => await catalog.Open(await Viewer(), kind, id, cancellation));
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
        try { return Ok(await operation()); }
        catch (HubError error) { return BadRequest(new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Action not confirmed. Check the result before retrying." }); }
    }
    [HttpGet("Status")]
    public Task<IActionResult> Status() => Safe(async () =>
    {
        var browser = await identity.Browser(User);
        return new { Linked = store.IsLinked(browser.UserId), Party = await parties.Status(browser), FeatureParityComplete = false };
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
    [HttpPost("Parties")]
    [Authorize(Policy = Policies.SyncPlayCreateGroup)]
    public Task<IActionResult> Create(CreateParty request, CancellationToken cancellation) => Safe(async () =>
    {
        var browser = await identity.Browser(User);
        var party = await parties.Create(browser, identity.Active(browser.UserId), request.Name, cancellation);
        return new { party.Id, GroupId = party.Group, GroupName = party.Name, party.Expires };
    });
    public sealed record JoinParty(string Ticket);
    [HttpPost("Parties/Join")]
    [Authorize(Policy = Policies.SyncPlayJoinGroup)]
    public Task<IActionResult> Join(JoinParty request, CancellationToken cancellation) => Safe(async () =>
        await parties.Join(await identity.Browser(User), request.Ticket, cancellation));
    [HttpPost("Parties/{id}/Close")]
    public Task<IActionResult> Close(string id) => Safe(async () =>
    {
        parties.Close(await identity.Browser(User), id); return new { Closed = true };
    });
    [HttpGet("Me")]
    public Task<IActionResult> Me() => Safe(async () => personal.State(identity.Active((await identity.Browser(User)).UserId)));
    [HttpGet("Me/Notifications")]
    public Task<IActionResult> Notifications() => Safe(async () => provider.GetRequiredService<NotificationFeed>().List(await Viewer()));
    [HttpPost("Me/Notifications/{id}/Seen")]
    public Task<IActionResult> SeenNotification(string id) => Safe(async () => { provider.GetRequiredService<NotificationFeed>().Seen(await Viewer(), id); return new { Saved = true }; });
    public sealed record ExternalWatchUpdate(long Revision, bool? Subscribed);
    [HttpPost("Me/Watchlist/{kind}/{id:int}")]
    public Task<IActionResult> ExternalWatch(string kind, int id, ExternalWatchUpdate update, CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = await Viewer();
        var recorded = await provider.GetRequiredService<WatchlistSync>().Follow(viewer, new(kind, id), update.Subscribed, update.Revision, async () => { await identity.Browser(User); }, cancellation);
        return personal.State(viewer) with { Activity = recorded };
    });
    [HttpPost("Me/Watchlist/Sync")]
    public Task<IActionResult> SyncWatchlist(CancellationToken cancellation) => Safe(async () =>
    {
        var viewer = await Viewer();
        return await provider.GetRequiredService<WatchlistSync>().Sync(viewer, async () => { await identity.Browser(User); }, cancellation);
    });
    public sealed record PersonalUpdate(long Revision, PersonalPreferences Preferences);
    [HttpPost("Me")]
    public Task<IActionResult> SavePersonal(PersonalUpdate update) => Safe(async () =>
        personal.Save(identity.Active((await identity.Browser(User)).UserId), update.Revision, update.Preferences));
    public sealed record BellUpdate(long Revision, bool Subscribed);
    [HttpPost("Me/Watchlist/{item:guid}/Bell")]
    public Task<IActionResult> Bell(Guid item, BellUpdate update) => Safe(async () =>
        personal.Bell(identity.Active((await identity.Browser(User)).UserId), item, update.Subscribed, update.Revision));
    public sealed record WatchlistRemove(long Revision);
    [HttpPost("Me/Watchlist/{item:guid}/Remove")]
    public Task<IActionResult> Remove(Guid item, WatchlistRemove update) => Safe(async () =>
        personal.Remove(identity.Active((await identity.Browser(User)).UserId), item, update.Revision));
    [HttpGet("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Settings() => Safe(async () => { await identity.Browser(User); return settings.Public(); });
    [HttpPost("Settings")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Save(SettingsUpdate update) => Safe(async () => { await identity.Browser(User); settings.Update(update); return settings.Public(); });
}
