using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Transforms the served response only; never edits Jellyfin Web files.</summary>
public sealed class ItemControlsRegistration(ILogger<ItemControlsRegistration> log) : IHostedService
{
    private static readonly Guid Id = Guid.Parse("c70297bf-77bd-4ad4-93c9-f67598519777");
    private Type? bridge;
    private bool registered;
    public static string Transform(JObject payload)
    {
        // This callback is invoked by another plugin, outside our controller/worker guards.
        var html = "";
        try
        {
            if (payload?["contents"] is not { Type: JTokenType.String } contents) return html;
            html = contents.Value<string>() ?? "";
            if (html.Length > 4 * 1024 * 1024 || html.Contains("data-mediahub-bootstrap", StringComparison.Ordinal)) return html;
            var closing = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            return closing < 0 ? html : html.Insert(closing, "<script data-mediahub-bootstrap defer src=\"ConfigurationPage?name=mediahub.items.js\"></script>");
        }
        catch { return html; } // A malformed companion payload must not break Jellyfin Web.
    }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            bridge = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")).FirstOrDefault(t => t is not null);
            var register = bridge?.GetMethod("RegisterTransformation", BindingFlags.Public | BindingFlags.Static, [typeof(JObject)]);
            if (register is null || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            register.Invoke(null, [JObject.FromObject(new { id = Id, fileNamePattern = @"(?:^|[\\/])index\.html$", callbackAssembly = GetType().Assembly.FullName,
                callbackClass = GetType().FullName, callbackMethod = nameof(Transform) })]);
            registered = true;
        }
        catch { log.LogWarning("Media Hub item controls unavailable. Jellyfin Web remains unchanged."); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (registered) bridge?.GetMethod("RemoveTransformation", [typeof(Guid)])?.Invoke(null, [Id]); }
        catch { log.LogWarning("Media Hub item-control registration cleanup failed."); }
        finally { registered = false; }
        return Task.CompletedTask;
    }
}
