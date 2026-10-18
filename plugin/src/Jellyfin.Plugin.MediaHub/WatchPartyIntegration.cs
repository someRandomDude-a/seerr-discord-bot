using Jellyfin.Plugin.WatchlistDeletion.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Compatibility status only. Party implementation lives in the optional integration plugin.</summary>
public sealed class WatchPartyIntegration(IServiceProvider services)
{
    public const string UnavailableCode = "watchparty_unavailable";
    public WatchPartyAvailability Status()
    {
        var ready = false;
        try { ready = services.GetKeyedService<Func<bool>>(CoreApi.DiscordReadyKey)?.Invoke() == true; }
        catch { /* Optional integrations never prevent standalone use. */ }
        return new(ready, false, ready ? "watchparty_registered" : UnavailableCode,
            ready ? "Refresh party status in Discord Integration to verify readiness." : "Install Discord Integration and Private SyncPlay for watch-party controls.");
    }
    public HubError Unavailable() => new("This older invitation workflow is retired. Use Watch together with Discord Integration and Private SyncPlay.", UnavailableCode);
}
public sealed record WatchPartyAvailability(bool Available, bool Active, string Code, string Message);
