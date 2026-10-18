using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub.DiscordIntegration;

[ApiController, Authorize, Route("MediaHub/WatchParty")]
[RequestSizeLimit(4096), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WatchPartyController(IServiceProvider services) : ControllerBase
{
    private async Task<IActionResult> Run(Func<WatchPartyIntegration, MediaBrowser.Controller.Session.SessionInfo, Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var actor = await services.GetRequiredService<HostIdentity>().Browser(User);
            return Ok(await action(services.GetRequiredService<WatchPartyIntegration>(), actor));
        }
        catch (WatchPartyFailure error) { return StatusCode(error.Status, new { Error = error.Message, error.Code }); }
        catch (HubError) { return StatusCode(403, new { Error = "A live signed-in Jellyfin user/player is required." }); }
        catch { return StatusCode(503, new { Error = "Watch-party result not confirmed. Refresh; do not blindly repeat a write.", Code = "watchparty_uncertain" }); }
    }
    [HttpGet, Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Current(CancellationToken cancellation) => Run(async (parties, actor) => await parties.Current(actor, cancellation));
    public sealed record CreateRequest(string Name);
    [HttpPost, Authorize(Policy = Policies.SyncPlayCreateGroup)]
    public Task<IActionResult> Create(CreateRequest request, CancellationToken cancellation) => Run(async (parties, actor) => await parties.Create(actor, request.Name, cancellation));
    [HttpPost("{party:guid}/Invites"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Invite(Guid party, CancellationToken cancellation) => Run(async (parties, actor) => await parties.Invite(actor, party, cancellation));
    [HttpPost("{party:guid}/Invites/{invite:guid}/Revoke"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Revoke(Guid party, Guid invite, CancellationToken cancellation) => Run(async (parties, actor) => new { Revoked = await parties.Revoke(actor, party, invite, cancellation) });
    [HttpPost("{party:guid}/Leave"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Leave(Guid party, CancellationToken cancellation) => Run(async (parties, actor) => new { Left = await parties.Leave(actor, party, cancellation) });
    [HttpPost("{party:guid}/End"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> End(Guid party, CancellationToken cancellation) => Run(async (parties, actor) => new { Ended = await parties.End(actor, party, cancellation) });
    [HttpGet("{party:guid}/Management"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Management(Guid party, CancellationToken cancellation) => Run(async (parties, actor) => await parties.Management(actor, party, cancellation));
    [HttpPost("{party:guid}/Policy"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Policy(Guid party, WatchPartyPolicyChange update, CancellationToken cancellation) => Run(async (parties, actor) => new { Accepted = await parties.UpdatePolicy(actor, party, update, cancellation) });
    public sealed record PermissionsRequest(string Revision, bool ControlPlayback, bool EditQueue);
    [HttpPost("{party:guid}/Accounts/{participant:guid}/Permissions"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Permissions(Guid party, Guid participant, PermissionsRequest update, CancellationToken cancellation) => Run(async (parties, actor) => new { Accepted = await parties.AccountPermissions(actor, party, participant, update.Revision, update.ControlPlayback, update.EditQueue, cancellation) });
    public sealed record ModerationRequest(string Revision, string Action, bool ConfirmSharedGuests = false);
    [HttpPost("{party:guid}/Participants/{participant:guid}/Moderate"), Authorize(Policy = Policies.SyncPlayHasAccess)]
    public Task<IActionResult> Moderate(Guid party, Guid participant, ModerationRequest update, CancellationToken cancellation) => Run(async (parties, actor) => new { Accepted = await parties.Moderate(actor, party, participant, update.Revision, update.Action, update.ConfirmSharedGuests, cancellation) });
}
