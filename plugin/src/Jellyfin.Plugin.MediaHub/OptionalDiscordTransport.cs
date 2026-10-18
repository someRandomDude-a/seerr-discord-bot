using System.Text.Json;

namespace Jellyfin.Plugin.MediaHub;

public interface INativePlaybackGuard
{
    Task Check(Guid user, string actor, string target, CancellationToken cancellation);
}
public sealed class OptionalPlaybackGuard(CoreApiProvider api) : INativePlaybackGuard
{
    public async Task Check(Guid user, string actor, string target, CancellationToken cancellation)
    {
        if (!JsonSerializer.Deserialize<bool>(await api.Transport("players.guard", new { User = user, Actor = actor, Target = target }, cancellation)))
            throw new HubError("Player authority was not confirmed. No command was sent.");
    }
}

/// <summary>Optional transport capability supplied by the separately installed Discord plugin.</summary>
public sealed class OptionalDiscordTransport(CoreApiProvider api):IDiscordMessaging
{
    private async Task<T> Invoke<T>(string operation,object payload,CancellationToken ct)=>JsonSerializer.Deserialize<T>(await api.Transport(operation,payload,ct))!;
    public async Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor,CancellationToken ct)=>await Invoke<MessageDestination[]>("messages.destinations",new{Actor=actor},ct);
    public async Task<IReadOnlyList<MessageTarget>> Members(OperatorActor actor,string guild,CancellationToken ct)=>await Invoke<MessageTarget[]>("messages.members",new{Actor=actor,Guild=guild},ct);
    public Task<MessageDestination> Verify(OperatorActor actor,MessageTarget target,bool writing,bool files,CancellationToken ct)=>Invoke<MessageDestination>("messages.verify",new{Actor=actor,Target=target,Writing=writing,Files=files},ct);
    public Task<string> Send(MessageTarget target,string content,IReadOnlyList<MessageBlob> files,CancellationToken ct)=>Invoke<string>("messages.send",new{Target=target,Content=content,Files=files},ct);
}
