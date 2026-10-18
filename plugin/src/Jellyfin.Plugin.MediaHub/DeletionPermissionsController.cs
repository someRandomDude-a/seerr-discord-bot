using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize(Policy = Policies.RequiresElevation), Route("MediaHub/Admin/DeletionPermissions")]
[RequestSizeLimit(65536), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DeletionPermissionsController(IServiceProvider services) : ControllerBase
{
    public sealed record Update(int Revision, DeletionPermissionSettings Permissions);
    private async Task<IActionResult> Run(Func<SettingsService, Task<object>> action)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var identity = services.GetRequiredService<HostIdentity>(); var session = await identity.Browser(User);
            void Administrator() { if (!identity.Active(session.UserId).HasPermission(PermissionKind.IsAdministrator)) throw new HubError("Native administrator access is required.", "admin_denied"); }
            Administrator(); var result = await action(services.GetRequiredService<SettingsService>()); await identity.Browser(User); Administrator(); return Ok(result);
        }
        catch (HubError error) { return StatusCode(error.Code == "admin_denied" ? 403 : 400, new { Error = error.Message }); }
        catch { return StatusCode(503, new { Error = "Deletion permissions could not be confirmed. Reload before saving again." }); }
    }
    private Task<object> View(SettingsService settings)
    {
        var value = settings.Current; var users = services.GetRequiredService<IUserManager>().GetUsers().Take(10001).ToArray();
        if (users.Length > 10000) throw new HubError("Account directory exceeds its supported budget.");
        return Task.FromResult<object>(new { value.Revision, Permissions = value.DeletionPermissions ?? new(), Accounts = users.Select(u => new { u.Id, Name = u.Username, Disabled = u.HasPermission(PermissionKind.IsDisabled) }).OrderBy(u => u.Name).ToArray() });
    }
    [HttpGet]
    public Task<IActionResult> Get() => Run(View);
    [HttpPost]
    public Task<IActionResult> Save(Update update) => Run(async settings =>
    {
        if (update.Permissions is null) throw new HubError("Specify deletion permissions.");
        DeletionPermissions.Validate(update.Permissions); var s = settings.Current;
        settings.Update(new(update.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null, DeletionPermissions: update.Permissions));
        return await View(settings);
    });
}
