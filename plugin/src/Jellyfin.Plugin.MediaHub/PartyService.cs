using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;

namespace Jellyfin.Plugin.MediaHub;

public interface IPartyMembership
{
    bool CreatorSessionJoined(string session, Guid user, Guid group);
}

public sealed class PartyService(HubStore store, HostIdentity identity, ISessionManager sessions, ISyncPlayManager syncPlay, IPartyMembership? membership = null)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> userLocks = new();
    public async Task<Party> Live(Party party, SessionInfo viewer)
    {
        var creator = await identity.Linked(party.Creator);
        if (party.State != "active" || party.Expires <= HubStore.Now) throw new HubError("This party has ended or expired.");
        if (!sessions.Sessions.Any(s => s.Id == party.CreatorSession && s.UserId == party.Creator))
            throw new HubError("The host's player is no longer connected. Ask them to create a new party.");
        var group = syncPlay.GetGroup(viewer, party.Group);
        if (group is null || !group.Participants.Contains(creator.Username, StringComparer.OrdinalIgnoreCase))
            throw new HubError("The party is no longer available to this player or its host has left. Ask the host to check the group and your library access.");
        // Jellyfin 12's public GetGroup DTO contains names, not member session IDs. A connected player plus
        // the same username in another session is NOT proof. Do not reflect into host-private dictionaries.
        if (membership?.CreatorSessionJoined(party.CreatorSession, party.Creator, party.Group) != true)
            throw new HubError("Exact host-player membership cannot be verified. Invitations are blocked; use native SyncPlay.");
        return party;
    }
    public async Task<Party> Create(SessionInfo creator, User user, string name, CancellationToken cancellation)
    {
        await identity.Linked(user.Id);
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new HubError("Use a party name of 1–80 characters.");
        if (user.SyncPlayAccess != SyncPlayUserAccessType.CreateAndJoinGroups) throw new HubError("Your Jellyfin account cannot create SyncPlay groups.");
        var gate = userLocks.GetOrAdd(user.Id, _ => new(1)); await gate.WaitAsync(cancellation);
        try
        {
            var existing = store.CreatorParty(user.Id);
            if (existing is not null)
            {
                if (existing.Expires > HubStore.Now && syncPlay.GetGroup(creator, existing.Group) is not null)
                    throw new HubError("You already have an active party. Reuse it or close its invitations first.");
                store.Close(existing.Id);
            }
            if (syncPlay.IsUserActive(user.Id)) throw new HubError("Leave your existing SyncPlay group before creating a new party.");
            cancellation.ThrowIfCancellationRequested();
            // No bot/synthetic session: the current authenticated browser creates the group.
            var group = syncPlay.NewGroup(creator, new NewGroupRequest(name.Trim()), CancellationToken.None);
            var party = new Party(HubStore.Nonce(), user.Id, creator.Id, group.GroupId, group.GroupName, HubStore.Now + 6 * 3600, "active");
            try { store.AddParty(party); }
            catch
            {
                // The player may have switched groups concurrently. Without exact membership proof,
                // a compensating LeaveGroup could kick it out of an unrelated group. Never guess.
                throw new HubError("Party recording failed. Check native SyncPlay before trying again; no automatic leave was sent.");
            }
            return party;
        }
        finally { gate.Release(); }
    }
    public async Task<Party> Creator(User user)
    {
        var party = store.CreatorParty(user.Id) ?? throw new HubError("Create a party in Jellyfin's Media Hub page first.");
        var session = sessions.Sessions.FirstOrDefault(s => s.Id == party.CreatorSession && s.UserId == user.Id)
            ?? throw new HubError("The host player disconnected. Create a new party in Jellyfin.");
        return await Live(party, session);
    }
    public async Task<object> Status(SessionInfo browser)
    {
        var party = store.CreatorParty(browser.UserId);
        if (party is null) return new { Active = false };
        try { await Live(party, browser); }
        catch (HubError) { return new { Active = false, NeedsRefresh = true }; }
        var group = syncPlay.GetGroup(browser, party.Group)!;
        return new { Active = true, party.Id, GroupId = party.Group, GroupName = group.GroupName,
            group.State, group.Participants, party.Expires, Creator = party.Creator };
    }
    public async Task<object> Join(SessionInfo browser, string ticket, CancellationToken cancellation)
    {
        var gate = userLocks.GetOrAdd(browser.UserId, _ => new(1)); await gate.WaitAsync(cancellation);
        try
        {
            await identity.Linked(browser.UserId);
            var user = identity.Active(browser.UserId);
            if (user.SyncPlayAccess == SyncPlayUserAccessType.None) throw new HubError("SyncPlay is disabled for your account.");
            var party = await Live(store.ConsumeTicket(ticket, browser.UserId), browser);
            if (syncPlay.IsUserActive(user.Id)) throw new HubError("Leave your existing SyncPlay session first, then obtain a new invitation link.");
            cancellation.ThrowIfCancellationRequested();
            syncPlay.JoinGroup(browser, new JoinGroupRequest(party.Group), CancellationToken.None);
            // Jellyfin JoinGroup can signal denial over WebSocket despite an HTTP
            // success. The web client confirms GroupJoined, not this response.
            return new { GroupId = party.Group, Accepted = true, Confirmed = false };
        }
        finally { gate.Release(); }
    }
    public void Close(SessionInfo browser, string id)
    {
        var party = store.GetParty(id);
        if (party.Creator != browser.UserId) throw new HubError("Only the party creator can close its invitations.");
        store.Close(id); // Do not kick other players or pretend Jellyfin has a destroy-group API.
    }
}
