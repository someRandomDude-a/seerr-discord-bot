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

/// <summary>Optional in-memory companion registration; never edits Jellyfin Web files/settings.</summary>
public sealed class PluginPageBridge : IPluginPageBridge
{
    private const string PageId = "media-hub-c70297bf-77bd-4ad4-93c9-f67598519777";
    private Type? integration;
    public bool Register()
    {
        integration = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface")).FirstOrDefault(type => type is not null);
        if (integration is null) return false;
        var register = integration.GetMethod("RegisterPage", BindingFlags.Public | BindingFlags.Static, [typeof(JObject)])
            ?? throw new InvalidOperationException("Incompatible Plugin Pages registration API");
        register.Invoke(null, [JObject.FromObject(new { id = PageId, url = "/web/ConfigurationPage?name=mediahub", displayText = "Media Hub", icon = "groups" })]);
        return true;
    }
    public void Remove() => integration?.GetMethod("RemovePage", [typeof(string)])?.Invoke(null, [PageId]);
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
            if (!Available) log.LogInformation("Media Hub: install a Jellyfin-12-compatible Plugin Pages build for the user-facing menu entry.");
        }
        catch { log.LogWarning("Media Hub page registration failed. Jellyfin continues without the optional Media Hub menu entry."); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (Available) bridge.Remove(); }
        catch { log.LogWarning("Media Hub page cleanup failed; no host settings or web files were changed."); }
        finally { Available = false; }
        return Task.CompletedTask;
    }
}
