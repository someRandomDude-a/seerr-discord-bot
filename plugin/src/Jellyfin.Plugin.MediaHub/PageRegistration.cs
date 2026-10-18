using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaHub;

public interface IPluginPageBridge
{
    bool Register();
    void Remove();
}

/// <summary>Required in-memory companion registration; never edits Jellyfin Web files/settings.</summary>
public sealed class PluginPageBridge(WebIntegration? dependencies = null) : IPluginPageBridge
{
    private readonly WebIntegration integrations = dependencies ?? new();
    private const string PageId = "media-hub-c70297bf-77bd-4ad4-93c9-f67598519777";
    internal static readonly string[] PageIds = [PageId, PageId + "-watchlist", PageId + "-notifications"];
    private Type? integration;
    public bool Register()
    {
        integration = integrations.PagesApi();
        if (integration is null || integrations.TransformationApi() is null) { integrations.Unavailable(WebIntegration.PagesId); return false; }
        try
        {
            foreach (var page in new[] { (Id: PageIds[0], Section: "deletions", Label: "Deletion queue", Icon: "delete_sweep"), (Id: PageIds[1], Section: "watchlist", Label: "Watchlist", Icon: "bookmark"), (Id: PageIds[2], Section: "notifications", Label: "Notifications", Icon: "notifications") })
                WebIntegration.Register(integration, false, JObject.FromObject(new { id = page.Id, url = "/web/ConfigurationPage?name=mediahub" + (page.Section.Length == 0 ? "" : "&hubSection=" + page.Section), displayText = page.Label, icon = page.Icon }));
            integrations.Registered(WebIntegration.PagesId);
        }
        catch { try { Remove(); } catch { /* Cleanup only our GUID-scoped registrations. */ } integrations.Unavailable(WebIntegration.PagesId, true); throw; }
        return true;
    }
    public void Remove()
    {
        try { foreach (var id in PageIds) integration?.GetMethod("RemovePage", [typeof(string)])?.Invoke(null, [id]); }
        finally { integrations.Unavailable(WebIntegration.PagesId); }
    }
}

/// <summary>No stateful constructors or unhandled startup/shutdown exceptions at the host boundary.</summary>
public sealed class PageRegistration(IPluginPageBridge bridge, ILogger<PageRegistration> log) : IHostedService
{
    public bool Available { get; private set; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!cancellationToken.IsCancellationRequested) Available = bridge.Register();
            if (!Available) log.SafeWarning("Media Hub: required File Transformation >= {TransformationMinimum} / Plugin Pages >= {PagesMinimum} are missing or incompatible; install their Jellyfin 12.2 builds and restart.", WebIntegration.TransformationMinimum, WebIntegration.PagesMinimum);
        }
        catch { log.SafeWarning("Media Hub page registration failed. Jellyfin continues; required Web integration remains unavailable."); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (Available) bridge.Remove(); }
        catch { log.SafeWarning("Media Hub page cleanup failed; no host settings or web files were changed."); }
        finally { Available = false; }
        return Task.CompletedTask;
    }
}
