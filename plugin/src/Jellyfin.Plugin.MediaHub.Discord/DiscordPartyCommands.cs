using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.MediaHub.DiscordIntegration;

/// <summary>Authenticated Discord transport to SDK operations; invitation admission stays entirely with the provider.</summary>
public sealed class DiscordPartyCommands(WatchPartyIntegration parties, Func<string, Task<SessionInfo>> resolve)
{
    private async Task<T> Bound<T>(string discord, Guid expectedUser, Func<SessionInfo, Task<T>> action)
    {
        var actor = await resolve(discord);
        if (actor.UserId != expectedUser) throw new HubError("Linked account changed. Reopen the command.");
        var user = actor.UserId; var session = actor.Id; var device = actor.DeviceId;
        var result = await action(actor);
        var current = await resolve(discord);
        if (current.UserId != user || current.Id != session || current.DeviceId != device)
            throw new HubError("Linked player changed. Check Private SyncPlay before any further action; no invitation was disclosed or retried.");
        return result;
    }
    public Task<WatchPartyState> Current(string discord, Guid expectedUser, CancellationToken token) => Bound(discord, expectedUser, actor => parties.Current(actor, token));
    public Task<WatchPartyCreated> Create(string discord, Guid expectedUser, string name, CancellationToken token) => Bound(discord, expectedUser, actor => parties.Create(actor, name, token));
    public Task<WatchPartyLink> Invite(string discord, Guid expectedUser, Guid party, CancellationToken token) => Bound(discord, expectedUser, actor => parties.Invite(actor, party, token));
    public Task<bool> Revoke(string discord, Guid expectedUser, Guid party, Guid invite, CancellationToken token) => Bound(discord, expectedUser, actor => parties.Revoke(actor, party, invite, token));
}
