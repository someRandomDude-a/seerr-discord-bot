using MediaBrowser.Common.Api;
using Jellyfin.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("MediaHub/Admin/Deletions")]
[ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DeletionMonitoringController(IServiceProvider services) : ControllerBase
{
    private async Task<IActionResult> Read(Func<DeletionMonitoring, Guid, object> read)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var identity = services.GetRequiredService<HostIdentity>(); var actor = await identity.Browser(User);
            var result = read(services.GetRequiredService<DeletionMonitoring>(), actor.UserId);
            await identity.Browser(User);
            if (!identity.Active(actor.UserId).HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
                throw new HubError("Native administrator access is required.", "admin_denied");
            return Ok(result);
        }
        catch (HubError error) when (error.Code == "admin_denied") { return StatusCode(403, new { Error = error.Message }); }
        catch (HubError error) { return BadRequest(new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Monitoring could not be verified. Retained state was not changed." }); }
    }
    [HttpGet]
    public Task<IActionResult> Browse(string state = "backlog", string query = "", Guid? owner = null, int page = 1, string sort = "oldest") =>
        Read((monitor, actor) => monitor.Browse(actor, state, query, owner, page, sort));
    [HttpGet("{id}")]
    public Task<IActionResult> Details(string id) => Read((monitor, actor) => monitor.Details(actor, id));
    [HttpGet("{id}/History")]
    public Task<IActionResult> History(string id, int page = 1) => Read((monitor, actor) => monitor.History(actor, id, page));
}
