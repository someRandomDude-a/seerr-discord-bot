using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize, Route("MediaHub/Players")]
[RequestSizeLimit(4096), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PlayerController(IServiceProvider services) : ControllerBase
{
    private async Task<IActionResult> Run(Func<NativePlayback, MediaBrowser.Controller.Session.SessionInfo, Task<object>> operation)
    {
        try { var actor = await services.GetRequiredService<HostIdentity>().Browser(User); var result = await operation(services.GetRequiredService<NativePlayback>(), actor); await services.GetRequiredService<HostIdentity>().Browser(User); return Ok(result); }
        catch (HubError error) { return BadRequest(new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Player result unconfirmed. Inspect the receipt; no automatic retry was performed." }); }
    }
    [HttpGet]
    public Task<IActionResult> Destinations() => Run((players, actor) => Task.FromResult<object>(players.Destinations(actor)));
    [HttpGet("History")]
    public Task<IActionResult> History() => Run((players, actor) => Task.FromResult<object>(players.History(actor)));
    public sealed record Selection(string Target, Guid Item);
    [HttpPost("Prepare")]
    public Task<IActionResult> Prepare(Selection selection) => Run((players, actor) => Task.FromResult<object>(players.Prepare(actor, selection.Target, selection.Item)));
    [HttpPost("{id}/Confirm")]
    public Task<IActionResult> Confirm(string id, CancellationToken cancellation) => Run(async (players, actor) => await players.Confirm(actor, id, async () => { await services.GetRequiredService<HostIdentity>().Browser(User); }, cancellation));
    [HttpPost("{id}/Cancel")]
    public Task<IActionResult> Cancel(string id) => Run((players, actor) => Task.FromResult<object>(players.Cancel(actor, id)));
    [HttpGet("{id}/Receipt")]
    public Task<IActionResult> Receipt(string id) => Run((players, actor) => Task.FromResult<object>(players.Receipt(actor, id)));
    [HttpPost("{id}/Acknowledge")]
    public Task<IActionResult> Acknowledge(string id) => Run((players, actor) => Task.FromResult<object>(players.Acknowledge(actor, id)));
}
