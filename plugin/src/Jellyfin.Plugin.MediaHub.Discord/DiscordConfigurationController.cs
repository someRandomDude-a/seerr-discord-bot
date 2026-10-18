using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("MediaHub/Discord/Configuration")]
[ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue), RequestSizeLimit(65536)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DiscordConfigurationController(IServiceProvider services) : ControllerBase
{
    private async Task Verify()
    {
        var identity = services.GetRequiredService<HostIdentity>();
        if (!identity.Active((await identity.Browser(User)).UserId).HasPermission(PermissionKind.IsAdministrator))
            throw new HubError("Native Jellyfin administrator access is required.", "admin_denied");
    }
    private async Task<IActionResult> Safe(Func<DiscordConfiguration, object> operation)
    {
        Response.Headers.CacheControl = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer";
        try { await Verify(); var value = operation(services.GetRequiredService<DiscordConfiguration>()); await Verify(); return Ok(value); }
        catch (HubError error) { return StatusCode(error.Code == "admin_denied" ? 403 : 400, new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Discord settings could not be verified. Reload before any further save; retained files were not reset." }); }
    }
    private object View(DiscordConfiguration configuration)
    {
        var view = JsonSerializer.SerializeToNode(configuration.Review())!.AsObject();
        view["PublicJellyfinUrl"] = services.GetRequiredService<SettingsService>().Current.PublicJellyfinUrl;
        return view;
    }
    [HttpGet] public Task<IActionResult> Read() => Safe(View);
    [HttpPost] public Task<IActionResult> Save(DiscordConfigurationUpdate update) => Safe(configuration => {
        if (update.DiscordEnabled || update.ActivityEnabled) _ = ServiceAddress.Url(services.GetRequiredService<SettingsService>().Current.PublicJellyfinUrl, https: true);
        configuration.Update(update); return View(configuration);
    });
}
