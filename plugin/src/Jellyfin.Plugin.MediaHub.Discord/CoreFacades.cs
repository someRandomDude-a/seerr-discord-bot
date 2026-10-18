global using Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.Claims;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.MediaHub;

public sealed class HubError(string message, string? code = null) : Exception(message) { public string? Code { get; } = code; }
public sealed class Rpc(IWatchlistDeletionClient client)
{
    private readonly ConcurrentDictionary<Guid,string> subjects = new();
    public void Bind(Guid user, string subject) { if(subjects.Count>=10000&&!subjects.ContainsKey(user))throw new HubError("Too many active integration actors."); subjects[user]=subject; }
    public async Task<T> Subject<T>(string subject, Guid? user, string op, object? value=null,CancellationToken ct=default)
    { try { return await client.InvokeAsync<T>(subject,user,op,value,ct); } catch(CoreApiException error){throw new HubError(error.Message,error.Code);} }
    public Task<T> Async<T>(Guid user,string op,object? value=null,CancellationToken ct=default)=>Subject<T>(subjects.TryGetValue(user,out var subject)?subject:throw new HubError("Verify your linked identity first."),user,op,value,ct);
    public T Call<T>(Guid user,string op,object? value=null)=>Async<T>(user,op,value).GetAwaiter().GetResult();
    public T Server<T>(string op,object? value=null)=>Subject<T>("",null,op,value).GetAwaiter().GetResult();
    public Task<T> ServerAsync<T>(string op,object? value=null,CancellationToken ct=default)=>Subject<T>("",null,op,value,ct);
}
public sealed class SettingsService(Rpc rpc)
{
    public GatewaySettings Current=>rpc.Server<GatewaySettings>("transport.settings");
    public string DiscordToken()=>rpc.Server<string>("transport.token");
    public string WebUrl()=>rpc.Server<string>("transport.webUrl");
}
public sealed class HubStore(Rpc rpc)
{
    public static string Nonce()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public string BeginLink(string subject)=>rpc.Subject<string>(subject,null,"transport.beginLink").GetAwaiter().GetResult();
}
public sealed class HostIdentity(Rpc rpc,IUserManager users,ISessionManager sessions)
{
    public User Active(Guid id)=>users.GetUserById(id) is { } user&&!user.HasPermission(PermissionKind.IsDisabled)?user:throw new HubError("Native account unavailable.");
    public async Task<SessionInfo> DiscordSession(string subject)
    {
        var actor=await rpc.Subject<LinkedActor>(subject,null,"identity.resolve");
        var session=sessions.Sessions.SingleOrDefault(row=>row.Id==actor.Session&&row.UserId==actor.User&&row.DeviceId==actor.Device)??throw new HubError("The linked native session is no longer available.");
        _=Active(actor.User);rpc.Bind(actor.User,subject);return session;
    }
    public async Task<User> Discord(string subject)=>Active((await DiscordSession(subject)).UserId);
    public async Task<SessionInfo> Browser(ClaimsPrincipal principal)
    {
        if(principal.Identity?.IsAuthenticated!=true||!bool.TryParse(principal.FindFirstValue("Jellyfin-IsApiKey"),out var apiKey)||apiKey||!Guid.TryParse(principal.FindFirstValue("Jellyfin-UserId"),out var id)||id==Guid.Empty)
            throw new HubError("Sign in with a Jellyfin user account, not an API key.");
        _=Active(id);var device=principal.FindFirstValue("Jellyfin-DeviceId")??"";var token=principal.FindFirstValue("Jellyfin-Token")??"";
        if(string.IsNullOrWhiteSpace(device)||string.IsNullOrWhiteSpace(token))throw new HubError("A signed-in native player is required.");
        var session=await sessions.GetSessionByAuthenticationToken(token,device,"127.0.0.1");
        if(session.UserId!=id||session.DeviceId!=device)throw new HubError("Native identity changed.");return session;
    }
}
public sealed class LinkFlow(Rpc rpc){public string HandoffUrl(string code)=>rpc.Server<string>("transport.handoff",new{Code=code});}
public sealed class DiscordControls(Rpc rpc)
{
    private readonly ConcurrentDictionary<string,(string Subject,Guid User)> views=new();
    public string Add(string view,string discord,Guid user,MediaControl payload){if(views.Count>=10000&&!views.ContainsKey(view))throw new HubError("Too many active views.");views[view]=(discord,user);return rpc.Subject<string>(discord,user,"controls.add",new{View=view,Control=payload}).GetAwaiter().GetResult();}
    public void Bind(string view,ulong message){if(!views.TryRemove(view,out var actor))return;_=rpc.Subject<bool>(actor.Subject,actor.User,"controls.bind",new{View=view,Message=message}).GetAwaiter().GetResult();}
    public MediaControl Claim(string id,string discord,Guid user,ulong message)=>rpc.Subject<MediaControl>(discord,user,"controls.claim",new{Id=id,Message=message}).GetAwaiter().GetResult();
}
public interface IViewerMedia{VisibleItem? Find(User user,Guid id);}
public sealed class ViewerMedia(Rpc rpc):IViewerMedia{public VisibleItem? Find(User user,Guid id)=>rpc.Call<VisibleItem>(user.Id,"media.find",new{Item=id});}
public sealed class PersonalService(Rpc rpc)
{
    public PersonalState State(User user)=>rpc.Call<PersonalState>(user.Id,"personal.state");
    public PersonalState Save(User user,long revision,PersonalPreferences preferences)=>rpc.Call<PersonalState>(user.Id,"personal.save",new{Revision=revision,Preferences=preferences});
    public PersonalState Add(User user,Guid item,long revision)=>rpc.Call<PersonalState>(user.Id,"personal.add",new{Item=item,Revision=revision});
    public PersonalState Bell(User user,Guid item,bool subscribed,long revision)=>rpc.Call<PersonalState>(user.Id,"personal.bell",new{Item=item,Subscribed=subscribed,Revision=revision});
    public PersonalState Remove(User user,Guid item,long revision)=>rpc.Call<PersonalState>(user.Id,"personal.remove",new{Item=item,Revision=revision});
}
public sealed class MediaActivity(Rpc rpc){public ActivityReceipt Record(User user,Guid item,MediaInteraction action)=>rpc.Call<ActivityReceipt>(user.Id,"media.activity",new{Item=item,Action=action});}
public sealed class MediaCatalog(Rpc rpc)
{
    public MediaPage Library(User u,string kind,string query,int page)=>rpc.Call<MediaPage>(u.Id,"catalog.library",new{Kind=kind,Query=query,Page=page});
    public Task<MediaPage> Browse(User u,string kind,string query,int page,CancellationToken ct)=>rpc.Async<MediaPage>(u.Id,"catalog.browse",new{Kind=kind,Query=query,Page=page},ct);
    public Task<MediaPage> Requests(User u,bool all,int page,CancellationToken ct)=>rpc.Async<MediaPage>(u.Id,"catalog.requests",new{All=all,Page=page},ct);
    public Task<MediaCard> Details(User u,string kind,int id,CancellationToken ct,bool interact=false)=>rpc.Async<MediaCard>(u.Id,"catalog.details",new{Kind=kind,Id=id,Interact=interact},ct);
    public MediaCard NativeDetails(User u,Guid id,bool interact=false)=>rpc.Call<MediaCard>(u.Id,"catalog.native",new{Item=id,Interact=interact});
    public IReadOnlyList<MediaEdition> Editions(User u,string kind,int id)=>rpc.Call<MediaEdition[]>(u.Id,"catalog.editions",new{Kind=kind,Id=id});
    public MediaOpen OpenEdition(User u,string kind,int id,Guid item)=>rpc.Call<MediaOpen>(u.Id,"catalog.openEdition",new{Kind=kind,Id=id,Item=item});
    public Task<RequestPermissions> Permissions(User u,CancellationToken ct)=>rpc.Async<RequestPermissions>(u.Id,"catalog.permissions",ct:ct);
    public Task<MediaOpen> Open(User u,string kind,int id,CancellationToken ct)=>rpc.Async<MediaOpen>(u.Id,"catalog.open",new{Kind=kind,Id=id},ct);
}
public sealed class PosterAssets(Rpc rpc) { public Task<MessageBlob> Download(User user,string kind,int id,CancellationToken ct)=>rpc.Async<MessageBlob>(user.Id,"catalog.poster",new{Kind=kind,Id=id},ct); }
public sealed class MediaRequests(Rpc rpc)
{
    public Task<RequestPlan> Prepare(User u,string kind,int id,bool is4k,int[]? seasons,CancellationToken ct)=>rpc.Async<RequestPlan>(u.Id,"requests.prepare",new{Kind=kind,Id=id,Is4k=is4k,Seasons=seasons},ct);
    public Task<RequestReceipt> Confirm(User u,string id,Func<Task> verify,CancellationToken ct)=>rpc.Async<RequestReceipt>(u.Id,"requests.confirm",new{Id=id},ct);
    public RequestReceipt Receipt(User u,string id)=>rpc.Call<RequestReceipt>(u.Id,"requests.receipt",new{Id=id});
    public RequestReceipt Cancel(User u,string id)=>rpc.Call<RequestReceipt>(u.Id,"requests.cancel",new{Id=id});
}
public sealed class ExtraMedia(Rpc rpc)
{
    public Task<ExtraPage> Search(User u,string kind,string query,int page,CancellationToken ct)=>rpc.Async<ExtraPage>(u.Id,"extras.search",new{Kind=kind,Query=query,Page=page},ct);
    public Task<ExtraCard> Details(User u,string kind,string foreign,bool interact,CancellationToken ct)=>rpc.Async<ExtraCard>(u.Id,"extras.details",new{Kind=kind,Foreign=foreign,Interact=interact},ct);
    public Task<RequestPlan> Prepare(User u,string kind,string foreign,string? edition,CancellationToken ct)=>rpc.Async<RequestPlan>(u.Id,"extras.prepare",new{Kind=kind,Foreign=foreign,Edition=edition},ct);
    public Task<RequestReceipt> Confirm(User u,string id,Func<Task> verify,CancellationToken ct)=>rpc.Async<RequestReceipt>(u.Id,"extras.confirm",new{Id=id},ct);
    public RequestReceipt Receipt(User u,string id)=>rpc.Call<RequestReceipt>(u.Id,"extras.receipt",new{Id=id});
    public RequestReceipt Cancel(User u,string id)=>rpc.Call<RequestReceipt>(u.Id,"extras.cancel",new{Id=id});
    public Task<ActivityReceipt?> Follow(User u,string kind,string foreign,bool? subscribed,long revision,Func<Task> verify,CancellationToken ct,bool saveOnly=false)=>rpc.Async<ActivityReceipt?>(u.Id,"extras.follow",new{Kind=kind,Foreign=foreign,Subscribed=subscribed,Revision=revision,SaveOnly=saveOnly},ct);
}
public sealed class WatchlistSync(Rpc rpc)
{
    public Task<ActivityReceipt?> Follow(User u,MediaKey key,bool? subscribed,long revision,Func<Task> verify,CancellationToken ct,bool saveOnly=false)=>rpc.Async<ActivityReceipt?>(u.Id,"watch.follow",new{Key=key,Subscribed=subscribed,Revision=revision,SaveOnly=saveOnly},ct);
    public Task<WatchSyncResult> Sync(User u,Func<Task> verify,CancellationToken ct)=>rpc.Async<WatchSyncResult>(u.Id,"watch.sync",ct:ct);
    public IReadOnlyList<WatchSyncIssue> Issues(User u)=>rpc.Call<WatchSyncIssue[]>(u.Id,"watch.issues");
    public Task<WatchSyncReview> Review(User u,MediaKey key,Func<Task> verify,CancellationToken ct)=>rpc.Async<WatchSyncReview>(u.Id,"watch.review",new{Key=key},ct);
    public Task<WatchSyncResult> AcceptRemote(User u,string token,Func<Task> verify,CancellationToken ct)=>rpc.Async<WatchSyncResult>(u.Id,"watch.accept",new{Token=token},ct);
    public Task<LegacyWatchReview> ReviewLegacy(User u,Func<Task> verify,CancellationToken ct)=>rpc.Async<LegacyWatchReview>(u.Id,"watch.legacyReview",ct:ct);
    public Task<bool> AcceptLegacy(User u,string token,Func<Task> verify,CancellationToken ct)=>rpc.Async<bool>(u.Id,"watch.legacyAccept",new{Token=token},ct);
}
public interface IRequestAuthority{Task<IReadOnlyList<OwnedRequest>> FindOwned(User u,Guid id,CancellationToken ct);}
public sealed class RequestAuthority(Rpc rpc):IRequestAuthority{public async Task<IReadOnlyList<OwnedRequest>> FindOwned(User u,Guid id,CancellationToken ct)=>await rpc.Async<OwnedRequest[]>(u.Id,"deletions.owned",new{Item=id},ct);}
public sealed class DeletionQueue(Rpc rpc)
{
    public Task<DeletionPermission> Permission(User u,CancellationToken ct)=>rpc.Async<DeletionPermission>(u.Id,"deletions.permission",ct:ct);
    public Task<bool> ProposeNative(User u,Guid item,CancellationToken ct)=>rpc.Async<bool>(u.Id,"deletions.proposeNative",new{Item=item},ct);
    public Task<PersonalDeletionStatus> Status(User u,string id,CancellationToken ct)=>rpc.Async<PersonalDeletionStatus>(u.Id,"deletions.status",new{Id=id},ct);
    public IReadOnlyList<QueueItem> List(User u)=>rpc.Call<QueueItem[]>(u.Id,"deletions.list");
    public Task<bool> Refresh(User u,CancellationToken ct)=>rpc.Async<bool>(u.Id,"deletions.refresh",ct:ct);
    public IReadOnlyList<ConsentNotice> Notices(User u)=>rpc.Call<ConsentNotice[]>(u.Id,"deletions.notices");
    public Task<bool> Propose(User u,int request,Guid item,CancellationToken ct)=>rpc.Async<bool>(u.Id,"deletions.propose",new{Request=request,Item=item},ct);
    public Task<bool> Respond(User u,string id,bool approve,CancellationToken ct)=>rpc.Async<bool>(u.Id,"deletions.respond",new{Id=id,Approve=approve},ct);
    public void Undo(User u,string id)=>_=rpc.Call<bool>(u.Id,"deletions.undo",new{Id=id});
}
public sealed class NativePlayback(Rpc rpc)
{
    public IReadOnlyList<PlayerDestination> Destinations(SessionInfo a)=>rpc.Call<PlayerDestination[]>(a.UserId,"players.destinations");
    public IReadOnlyList<PlaybackHistory> History(SessionInfo a)=>rpc.Call<PlaybackHistory[]>(a.UserId,"players.history");
    public PlaybackPreview Prepare(SessionInfo a,string target,Guid item)=>rpc.Call<PlaybackPreview>(a.UserId,"players.prepare",new{Target=target,Item=item});
    public Task<PlaybackReceipt> Confirm(SessionInfo a,string id,Func<Task> verify,CancellationToken ct)=>rpc.Async<PlaybackReceipt>(a.UserId,"players.confirm",new{Id=id},ct);
    public PlaybackReceipt Cancel(SessionInfo a,string id)=>rpc.Call<PlaybackReceipt>(a.UserId,"players.cancel",new{Id=id});
    public PlaybackReceipt Acknowledge(SessionInfo a,string id)=>rpc.Call<PlaybackReceipt>(a.UserId,"players.acknowledge",new{Id=id});
    public PlaybackReceipt Receipt(SessionInfo a,string id)=>rpc.Call<PlaybackReceipt>(a.UserId,"players.receipt",new{Id=id});
}
public sealed class SeerrBridge(Rpc rpc){public async Task<SeerrScope> Open(User u,CancellationToken ct)=>new(rpc,u.Id,await rpc.Async<SeerrViewer>(u.Id,"seerr.viewer",ct:ct));}
public sealed class SeerrScope(Rpc rpc,Guid user,SeerrViewer viewer):IDisposable{public SeerrViewer Viewer=>viewer;public bool Can(long flags)=>(viewer.Permissions&(2|flags))!=0;public Task<SeerrViewer> Verify(CancellationToken ct)=>rpc.Async<SeerrViewer>(user,"seerr.viewer",ct:ct);public void Dispose(){} }
public sealed class ArrRegistry(Rpc rpc){public async Task<IReadOnlyList<ServiceSpace>> Storage(User u,CancellationToken ct)=>await rpc.Async<ServiceSpace[]>(u.Id,"storage",ct:ct);}
public sealed class OperatorAuthority(Rpc rpc){public Task<OperatorActor> Require(Guid user,CancellationToken ct)=>rpc.Async<OperatorActor>(user,"operator.require",ct:ct);public async Task Check(OperatorActor actor,CancellationToken ct){if(await Require(actor.User,ct)!=actor)throw new HubError("Operator identity changed.","operator_denied");}}
