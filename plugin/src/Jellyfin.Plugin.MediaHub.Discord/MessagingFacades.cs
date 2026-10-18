using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

public interface IDiscordMessaging
{
    Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor,CancellationToken ct);
    Task<IReadOnlyList<MessageTarget>> Members(OperatorActor actor,string guild,CancellationToken ct);
    Task<MessageDestination> Verify(OperatorActor actor,MessageTarget target,bool writing,bool files,CancellationToken ct);
    Task<string> Send(MessageTarget target,string content,IReadOnlyList<MessageBlob> files,CancellationToken ct);
}
public sealed class CallbackRegistry
{
    private readonly ConcurrentDictionary<string,Func<string,string,CancellationToken,Task>> callbacks=new();
    public string Add(Func<string,string,CancellationToken,Task> callback){if(callbacks.Count>=8)throw new HubError("Notification delivery is busy.");var id=HubStore.Nonce();if(!callbacks.TryAdd(id,callback))throw new HubError("Notification delivery unavailable.");return id;}
    public void Remove(string id)=>callbacks.TryRemove(id,out _);
    public async Task Run(string encoded,CancellationToken ct){using var doc=JsonDocument.Parse(encoded);var p=doc.RootElement;var id=p.GetProperty("Callback").GetString()!;if(!callbacks.TryGetValue(id,out var callback))throw new HubError("Delivery caller expired. No resend was made.");await callback(p.GetProperty("Discord").GetString()!,p.GetProperty("Notice").GetRawText(),ct);}
}
public sealed class ConsentDelivery(Rpc rpc,CallbackRegistry callbacks)
{
    public async Task Deliver(Func<string,ConsentNotice,CancellationToken,Task> send,CancellationToken ct){var key=callbacks.Add((id,json,token)=>send(id,JsonSerializer.Deserialize<ConsentNotice>(json)!,token));try{await rpc.ServerAsync<bool>("delivery.consent",new{Callback=key},ct);}finally{callbacks.Remove(key);}}
}
public sealed class NotificationFeed(Rpc rpc,CallbackRegistry callbacks)
{
    public async Task<IReadOnlyList<HubNotification>> ListAsync(Jellyfin.Database.Implementations.Entities.User user,CancellationToken ct)=>await rpc.Async<HubNotification[]>(user.Id,"notifications.list",ct:ct);
    public void Seen(Jellyfin.Database.Implementations.Entities.User user,string id)=>_=rpc.Call<bool>(user.Id,"notifications.seen",new{Id=id});
    public NotificationDiagnostics Diagnostics(Jellyfin.Database.Implementations.Entities.User user)=>rpc.Call<NotificationDiagnostics>(user.Id,"notifications.diagnostics");
    public async Task Deliver(Func<string,HubNotification,CancellationToken,Task> send,CancellationToken ct){var key=callbacks.Add((id,json,token)=>send(id,JsonSerializer.Deserialize<HubNotification>(json)!,token));try{await rpc.ServerAsync<bool>("delivery.notifications",new{Callback=key},ct);}finally{callbacks.Remove(key);}}
}
public sealed class MessageAssets(Rpc rpc){public static bool Allowed(string url)=>Jellyfin.Plugin.WatchlistDeletion.Api.MediaPresentation.DiscordAsset(url);public Task<MessageBlob> Download(string url,string filename,CancellationToken ct)=>rpc.ServerAsync<MessageBlob>("transport.download",new{Url=url,Filename=filename},ct);}
public sealed class Messaging(Rpc rpc)
{
    public const int FileLimit=8*1024*1024;
    public static string Filename(string name)=>Jellyfin.Plugin.WatchlistDeletion.Api.MediaPresentation.Filename(name);
    public Task<bool> Deliver(CancellationToken ct)=>rpc.ServerAsync<bool>("delivery.messages",ct:ct);
    public void Receive(MessageView message,JsonNode rich,bool direct,bool mentioned,bool reply)=>_=rpc.Server<bool>("transport.receive",new{Message=message,Rich=rich,Direct=direct,Mentioned=mentioned,IsReply=reply});
    public void Edit(string id,string channel,string content,JsonNode rich)=>_=rpc.Server<bool>("transport.edit",new{Id=id,Channel=channel,Content=content,Rich=rich});
    public void Delete(string id,string channel)=>_=rpc.Server<bool>("transport.delete",new{Id=id,Channel=channel});
    public async Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor a,CancellationToken ct)=>await rpc.Async<MessageDestination[]>(a.User,"messages.destinations",ct:ct);
    public Task<MessageUpload> Upload(OperatorActor a,string filename,byte[] body,CancellationToken ct)=>rpc.Async<MessageUpload>(a.User,"messages.upload",new{Filename=filename,Bytes=body},ct);
    public Task<MessageBlob> DownloadUpload(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<MessageBlob>(a.User,"messages.uploadRead",new{Id=id},ct);
    public Task<SendPreview> Prepare(OperatorActor a,string mode,string content,MessageTarget[] targets,string[] uploads,string? request,bool members,CancellationToken ct)=>rpc.Async<SendPreview>(a.User,"messages.prepare",new{Mode=mode,Content=content,Targets=targets,Uploads=uploads,Request=request,Members=members},ct);
    public Task<SendPreview> GetPreview(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<SendPreview>(a.User,"messages.preview",new{Id=id},ct);
    public Task<SendReceipt> Confirm(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<SendReceipt>(a.User,"messages.confirm",new{Id=id},ct);
    public Task<bool> Cancel(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<bool>(a.User,"messages.cancel",new{Id=id},ct);
    public Task<SendReceipt> Receipt(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<SendReceipt>(a.User,"messages.receipt",new{Id=id},ct);
    public async Task<IReadOnlyList<SendReceipt>> History(OperatorActor a,int page,CancellationToken ct)=>await rpc.Async<SendReceipt[]>(a.User,"messages.history",new{Page=page},ct);
    public Task<InboxPage> Inbox(OperatorActor a,string kind,string? guild,string? peer,string? channel,string query,int page,bool oldest,CancellationToken ct)=>rpc.Async<InboxPage>(a.User,"messages.inbox",new{Kind=kind,Guild=guild,Peer=peer,Channel=channel,Query=query,Page=page,Oldest=oldest},ct);
    public Task<MessageView> Message(OperatorActor a,string id,CancellationToken ct)=>rpc.Async<MessageView>(a.User,"messages.message",new{Id=id},ct);
    public async Task<IReadOnlyDictionary<string,long>> Diagnostics(OperatorActor a,CancellationToken ct)=>await rpc.Async<Dictionary<string,long>>(a.User,"messages.diagnostics",ct:ct);
    private sealed record AssetView(string Url,string Filename,string Revision);
    public async Task<(string Url,string Filename,string Revision)> Asset(OperatorActor a,string id,string key,CancellationToken ct){var value=await rpc.Async<AssetView>(a.User,"messages.asset",new{Id=id,Key=key},ct);return(value.Url,value.Filename,value.Revision);}
    public Task<MessageBlob> SentUpload(OperatorActor a,string id,string file,CancellationToken ct)=>rpc.Async<MessageBlob>(a.User,"messages.sentUpload",new{Id=id,File=file},ct);
}
