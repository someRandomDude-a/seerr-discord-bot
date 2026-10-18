using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Transforms the served response only; never edits Jellyfin Web files.</summary>
public sealed class ItemControlsRegistration(ILogger<ItemControlsRegistration> log, WebIntegration? dependencies = null) : IHostedService
{
    private readonly WebIntegration integrations = dependencies ?? new();
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
            bridge = integrations.TransformationApi();
            if (bridge is null || integrations.PagesApi() is null || cancellationToken.IsCancellationRequested)
            {
                integrations.Unavailable(WebIntegration.TransformationId);
                log.LogWarning("Media Hub requires ABI-compatible File Transformation >= {TransformationMinimum} and Plugin Pages >= {PagesMinimum}. Web controls were not registered; the host is unchanged.", WebIntegration.TransformationMinimum, WebIntegration.PagesMinimum);
                return Task.CompletedTask;
            }
            // Share Plugin Pages' exact index.html pipeline. File Transformation selects
            // exact keys before regex keys; a separate regex would be shadowed by it.
            WebIntegration.Register(bridge, true, JObject.FromObject(new { id = Id, fileNamePattern = "index.html", callbackAssembly = GetType().Assembly.FullName,
                callbackClass = GetType().FullName, callbackMethod = nameof(Transform) }));
            registered = true;
            integrations.Registered(WebIntegration.TransformationId);
        }
        catch { integrations.Unavailable(WebIntegration.TransformationId, true); log.LogWarning("Media Hub dependency registration failed. Check the required native Web plugins and restart Jellyfin; no host files were edited."); }
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { if (registered) bridge?.GetMethod("RemoveTransformation", [typeof(Guid)])?.Invoke(null, [Id]); }
        catch { log.LogWarning("Media Hub item-control registration cleanup failed."); }
        finally { registered = false; integrations.Unavailable(WebIntegration.TransformationId); }
        return Task.CompletedTask;
    }
}
