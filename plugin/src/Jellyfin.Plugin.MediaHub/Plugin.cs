using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.WatchlistDeletion.Api;

namespace Jellyfin.Plugin.MediaHub;

public sealed class Plugin : BasePlugin, IHasWebPages
{
    public Plugin(IApplicationPaths paths)
    {
        // BasePlugin (unlike BasePlugin<T>) leaves these metadata fields unset.
        // Do not open state or create directories during host plugin discovery.
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.Combine(paths.DataPath, "media-hub"), assembly.GetName().Version!);
    }
    public override string Name => "Watchlist Deletion (Preview)";
    public override Guid Id => Guid.Parse("c70297bf-77bd-4ad4-93c9-f67598519777");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "mediahub", EmbeddedResourcePath = GetType().Namespace + ".Web.hub.html" },
        new() { Name = "mediahub.js", EmbeddedResourcePath = GetType().Namespace + ".Web.hub.js" },
        new() { Name = "mediahub.settings.js", EmbeddedResourcePath = GetType().Namespace + ".Web.settings.js" },
        new() { Name = "mediahub.monitoring.js", EmbeddedResourcePath = GetType().Namespace + ".Web.monitoring.js" },
        new() { Name = "mediahub.permissions.js", EmbeddedResourcePath = GetType().Namespace + ".Web.permissions.js" },
        new() { Name = "mediahub.items.js", EmbeddedResourcePath = GetType().Namespace + ".Web.items.js" },
        new() { Name = "mediahub.messenger.js", EmbeddedResourcePath = GetType().Namespace + ".Web.messenger.js" },
        new() { Name = "mediahub.watchparty.js", EmbeddedResourcePath = GetType().Namespace + ".Web.watchparty.js" }
    ];
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<HubRequestBoundary>();
        services.AddSingleton<HubStore>();
        services.AddSingleton<DiscordConfigurationOverlay>();
        services.AddSingleton<SettingsService>(provider => new(provider.GetRequiredService<HubStore>()) { Overlay = provider.GetRequiredService<DiscordConfigurationOverlay>().Apply });
        services.AddSingleton<HostIdentity>();
        services.AddSingleton<WatchPartyIntegration>();
        services.AddSingleton<IViewerMedia, JellyfinViewerMedia>();
        services.AddSingleton<MediaActivity>();
        services.AddSingleton<IRecentActivity>(provider => provider.GetRequiredService<MediaActivity>());
        services.AddSingleton<IRequestAuthority, RequestAuthority>();
        services.AddSingleton<DeletionPermissions>();
        services.AddSingleton<NativeDeletionReader>();
        services.AddSingleton<ArrDeletion>();
        services.AddSingleton<IArrDeletion>(provider => provider.GetRequiredService<ArrDeletion>());
        services.AddSingleton<INativeDeletionResolver>(provider => provider.GetRequiredService<ArrDeletion>());
        services.AddSingleton<DeletionQueue>();
        services.AddSingleton<DeletionMonitoring>();
        services.AddSingleton<ConsentDelivery>();
        services.AddHostedService<DeletionMaintenance>();
        services.AddSingleton<PersonalService>();
        services.AddSingleton<SeerrBridge>();
        services.AddSingleton<MediaCatalog>();
        services.AddSingleton<MediaRequests>();
        services.AddSingleton<ServiceHttp>();
        services.AddSingleton<ArrRegistry>();
        services.AddSingleton<ConfigurationDiscovery>();
        services.AddSingleton<ExtraMedia>();
        services.AddSingleton<OperatorAuthority>();
        services.AddSingleton<IDiscordOAuth, OptionalDiscordOAuth>();
        services.AddSingleton<LinkFlow>();
        services.AddSingleton<CoreApiProvider>();
        services.AddKeyedSingleton<Func<string,string,string,CancellationToken,Task<string>>>(CoreApi.ServiceKey,(provider,_)=>provider.GetRequiredService<CoreApiProvider>().Invoke);
        services.AddSingleton<IDiscordMessaging, OptionalDiscordTransport>();
        services.AddSingleton<Messaging>();
        services.AddSingleton<MessageAssets>();
        services.AddSingleton<DiscordControls>();
        services.AddSingleton<IExternalMedia, ExternalMedia>();
        services.AddSingleton<WatchlistSync>();
        services.AddSingleton<INotificationStatus, NotificationStatus>();
        services.AddSingleton<NotificationFeed>();
        services.AddSingleton<NotificationSignals>();
        services.AddSingleton<PosterAssets>();
        services.AddSingleton<NativePlayback>();
        services.AddSingleton<INativePlaybackGuard, OptionalPlaybackGuard>();
        services.AddHostedService<MediaMaintenance>();
        services.AddSingleton<IPluginPageBridge, PluginPageBridge>();
        services.AddSingleton<WebIntegration>();
        services.AddHostedService<ItemControlsRegistration>();
        services.AddSingleton<PageRegistration>();
        services.AddHostedService(provider => provider.GetRequiredService<PageRegistration>());
    }
}
