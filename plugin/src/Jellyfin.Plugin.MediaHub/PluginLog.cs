using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>A failed host logging sink must not turn an already-contained plugin failure into a host-stopping exception.</summary>
internal static class PluginLog
{
    internal static void SafeWarning(this ILogger logger, string template, params object?[] arguments)
    {
#pragma warning disable CA2254 // Call sites supply fixed developer-authored templates, never upstream errors or credentials.
        try { logger.LogWarning(template, arguments); }
#pragma warning restore CA2254
        catch { /* No fallback files, host reconfiguration or recursive logging. */ }
    }
}
