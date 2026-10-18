using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("MediaHub/Configuration")]
[RequestSizeLimit(4096), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ConfigurationController(IServiceProvider services) : ControllerBase
{
    private async Task Verify()
    {
        var identity = services.GetRequiredService<HostIdentity>();
        if (!identity.Active((await identity.Browser(User)).UserId).HasPermission(PermissionKind.IsAdministrator)) throw new UnauthorizedAccessException();
    }
    private async Task<IActionResult> Read(Func<ConfigurationDiscovery, Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { await Verify(); var result = await action(services.GetRequiredService<ConfigurationDiscovery>()); await Verify(); return Ok(result); }
        catch (UnauthorizedAccessException) { return StatusCode(403, new { Error = "Jellyfin administrator access is required." }); }
        catch (HubError error) { return BadRequest(new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Saved service options could not be verified. No settings or upstream data were changed." }); }
    }
    [HttpGet("Options/{service}")]
    public Task<IActionResult> Options(string service, int revision, CancellationToken cancellation) => Read(async discovery => await discovery.Options(service, revision, cancellation));
    [HttpGet("Destinations")]
    public Task<IActionResult> Destinations(int revision, CancellationToken cancellation) => Read(async discovery => await discovery.Destinations(revision, cancellation));
}
