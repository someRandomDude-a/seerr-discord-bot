using System.Text.Json;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub.DiscordIntegration;

public sealed class Plugin:BasePlugin, IHasWebPages
{
    public Plugin(IApplicationPaths paths)
    {
        var assembly=GetType().Assembly;
        SetAttributes(assembly.Location,Path.Combine(paths.DataPath,"media-hub-discord"),assembly.GetName().Version!);
    }
    public override string Name=>"Media Hub Discord Integration (Preview)";
    public override Guid Id=>Guid.Parse("8456d36a-3c89-4b6d-9959-9a74566cc252");
    public IEnumerable<PluginPageInfo> GetPages() => [
        new() { Name = "mediahub-discord", EmbeddedResourcePath = "Jellyfin.Plugin.MediaHub.Discord.Web.settings.html" },
        new() { Name = "mediahub-discord.js", EmbeddedResourcePath = "Jellyfin.Plugin.MediaHub.Discord.Web.settings.js" }
    ];
}
public sealed class IntegrationDependencies(IWatchlistDeletionClient core,IWatchPartyService parties)
{
    public bool Ready=>core.IsAvailable&&parties.IsAvailable;
}
public sealed class ServiceRegistrator:IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services,IServerApplicationHost host)
    {
        services.AddWatchlistDeletionClient().AddPrivateSyncPlayClient();
        services.AddSingleton<HubRequestBoundary>();
        services.AddSingleton<IntegrationDependencies>();services.AddSingleton<Rpc>();services.AddSingleton<SettingsService>();services.AddSingleton<HubStore>();services.AddSingleton<HostIdentity>();services.AddSingleton<LinkFlow>();services.AddSingleton<DiscordControls>();services.AddSingleton<IViewerMedia,ViewerMedia>();
        services.AddSingleton<DiscordConfiguration>();
        services.AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (provider, _) => provider.GetRequiredService<DiscordConfiguration>().SnapshotJson);
        services.AddSingleton<WatchPartyIntegration>(provider=>new(provider.GetRequiredService<IWatchPartyService>(),provider.GetRequiredService<MediaBrowser.Controller.Session.ISessionManager>(),
            id=>{try{provider.GetRequiredService<HostIdentity>().Active(id);return true;}catch{return false;}},
            ()=>provider.GetRequiredService<SettingsService>().Current.PublicJellyfinUrl,
            value=>provider.GetRequiredService<Rpc>().Server<string>("parties.review",new{Value=value}),
            ()=>provider.GetRequiredService<IWatchlistDeletionClient>().IsAvailable));
        services.AddKeyedSingleton<Func<bool>>(CoreApi.DiscordReadyKey,(provider,_)=>()=>provider.GetRequiredService<IntegrationDependencies>().Ready);
        services.AddSingleton<DiscordPartyCommands>(provider=>new(provider.GetRequiredService<WatchPartyIntegration>(),provider.GetRequiredService<HostIdentity>().DiscordSession));
        services.AddSingleton<DiscordOAuth>(provider=>new(()=>provider.GetRequiredService<Rpc>().Server<OAuthConfiguration>("transport.oauthConfiguration")));
        services.AddSingleton<IDiscordOAuth>(provider=>provider.GetRequiredService<DiscordOAuth>());
        services.AddSingleton<ActivitySessions>(); services.AddSingleton<PosterAssets>();
        services.AddSingleton<PersonalService>();services.AddSingleton<MediaActivity>();services.AddSingleton<MediaCatalog>();services.AddSingleton<MediaRequests>();services.AddSingleton<ExtraMedia>();services.AddSingleton<WatchlistSync>();services.AddSingleton<IRequestAuthority,RequestAuthority>();services.AddSingleton<DeletionQueue>();services.AddSingleton<NativePlayback>();services.AddSingleton<SeerrBridge>();services.AddSingleton<ArrRegistry>();services.AddSingleton<OperatorAuthority>();
        services.AddSingleton<CallbackRegistry>();services.AddSingleton<ConsentDelivery>();services.AddSingleton<NotificationFeed>();services.AddSingleton<MessageAssets>();services.AddSingleton<Messaging>();services.AddSingleton<IDiscordMessaging,DiscordMessaging>();services.AddSingleton<DiscordHost>();services.AddHostedService(provider=>provider.GetRequiredService<DiscordHost>());
        services.AddKeyedSingleton<Func<string,string,CancellationToken,Task<string>>>(CoreApi.DiscordTransportKey,(provider,_)=>async(operation,encoded,ct)=>
        {
            if(encoded.Length>24*1024*1024)throw new HubError("Transport input exceeds its budget.");
            if(operation.StartsWith("callback.",StringComparison.Ordinal)){await provider.GetRequiredService<CallbackRegistry>().Run(encoded,ct);return "true";}
            using var doc=JsonDocument.Parse(encoded);var p=doc.RootElement;T Read<T>(string name)=>p.GetProperty(name).Deserialize<T>()!;
            if (operation.StartsWith("oauth.",StringComparison.Ordinal))
            {
                if (!provider.GetRequiredService<IntegrationDependencies>().Ready) throw new HubError("OAuth requires both integration dependencies.");
                var oauth=provider.GetRequiredService<DiscordOAuth>(); object value;
                if (operation=="oauth.exchange") value=await oauth.Exchange(Read<string>("Code"),Read<string?>("Redirect"),Read<string?>("Verifier"),ct);
                else if(operation=="oauth.verify") value=await oauth.Verify(Read<string>("AccessToken"),Read<string?>("Guild"),Read<bool>("Activity"),ct);
                else if(operation=="oauth.revoke") { await oauth.Revoke(Read<string>("AccessToken"),ct); value=true; }
                else throw new HubError("Unsupported OAuth operation.");
                return JsonSerializer.Serialize(value);
            }
            if(operation=="players.guard")
            {
                var parties=provider.GetRequiredService<IWatchPartyService>();
                if(!provider.GetRequiredService<IntegrationDependencies>().Ready)throw new HubError("Both integration dependencies must be available before controlling a player.");
                var user=Read<Guid>("User");var actor=Read<string>("Actor");var target=Read<string>("Target");
                provider.GetRequiredService<HostIdentity>().Active(user);
                if(await parties.GetCurrentPartyAsync(user,actor,ct)is not null||await parties.GetCurrentPartyAsync(user,target,ct)is not null)
                    throw new HubError("Use Private SyncPlay’s own playback/queue controls while either player is in a party.");
                return "true";
            }
            var transport=provider.GetRequiredService<IDiscordMessaging>();object result=operation switch
            {
                "messages.destinations"=>await transport.Destinations(Read<OperatorActor>("Actor"),ct),
                "messages.members"=>await transport.Members(Read<OperatorActor>("Actor"),Read<string>("Guild"),ct),
                "messages.verify"=>await transport.Verify(Read<OperatorActor>("Actor"),Read<MessageTarget>("Target"),Read<bool>("Writing"),Read<bool>("Files"),ct),
                "messages.send"=>await transport.Send(Read<MessageTarget>("Target"),Read<string>("Content"),Read<MessageBlob[]>("Files"),ct),
                _=>throw new HubError("Unsupported transport operation.")
            };return JsonSerializer.Serialize(result);
        });
    }
}
