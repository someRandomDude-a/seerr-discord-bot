using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Session;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaHub.DiscordIntegration;

/// <summary>Typed SDK boundary only. Private SyncPlay owns playback, membership and invitation admission.</summary>
public sealed class WatchPartyIntegration(IWatchPartyService provider, ISessionManager sessions,
    Func<Guid, bool> activeUser, Func<string> publicUrl, Func<string, string> signReview, Func<bool> coreAvailable)
{
    public const string UnavailableCode = "watchparty_unavailable";
    public const string UnavailableMessage = "Install and enable matching Watchlist Deletion API 1.0 and Private SyncPlay 0.1.5 / API 1.1 on Jellyfin 12.2. Refresh to verify provider readiness.";
    private readonly SemaphoreSlim gate = new(1);

    public WatchPartyAvailability Status()
    {
        try { if (coreAvailable() && provider.IsAvailable) return new(true, false, "watchparty_registered", "Private SyncPlay is registered. Refresh party status to verify readiness."); }
        catch { /* Registration is not a health guarantee. */ }
        return new(false, false, UnavailableCode, UnavailableMessage);
    }
    // Retired Media Hub routes/Discord controls never redeem legacy invitations/tickets.
    public HubError Unavailable() => new("This older Media Hub invitation workflow is retired. Use Watch together in Jellyfin with Private SyncPlay.", UnavailableCode);

    private void Actor(SessionInfo session)
    {
        if (!activeUser(session.UserId) || string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.DeviceId) ||
            sessions.Sessions.Count(s => s.Id == session.Id && s.UserId == session.UserId && s.DeviceId == session.DeviceId) != 1)
            throw new WatchPartyFailure(403, "Open Media Hub in your existing signed-in Jellyfin player.");
    }
    private async Task<T> Run<T>(SessionInfo actor, Func<CancellationToken, Task<T>> operation, CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(0, cancellation)) throw new WatchPartyFailure(429, "A watch-party operation is still running. Refresh later; no second operation was started.");
        Task<T>? pending = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            Actor(actor);
            if (!Status().Available) throw new WatchPartyFailure(503, UnavailableMessage, UnavailableCode);
            // Invoke off the request thread as well: an in-process provider can block before
            // returning its Task. One retained slot bounds this exposure if it ignores cancellation.
            var token = budget.Token;
            pending = Task.Run(() => operation(token), CancellationToken.None);
            var result = await pending.WaitAsync(budget.Token);
            Actor(actor); // No late private result after account disable/disconnect.
            return result;
        }
        catch (WatchPartyFailure) { throw; }
        catch (HubError) { throw new WatchPartyFailure(403, "Your signed-in Jellyfin account/player could not be verified."); }
        catch (WatchPartyException error)
        {
            // Provider replies are not trusted presentation text; never reflect a token/code or exception.
            var message = error.StatusCode switch
            {
                400 => "Private SyncPlay rejected the settings or invitation limits.",
                403 => "Your current player, ownership or Private SyncPlay policy does not permit this action.",
                404 => "This party or invitation is no longer available. Refresh its state.",
                409 => "Your player is already in a party, or its capacity was reached. Refresh its state.",
                429 => "Private SyncPlay is limiting requests. Wait and refresh; no automatic retry was performed.",
                _ => "Private SyncPlay could not confirm the result. Refresh before any further action; do not blindly repeat a write."
            };
            throw new WatchPartyFailure(error.StatusCode is 400 or 403 or 404 or 409 or 429 ? error.StatusCode : 503, message);
        }
        catch
        {
            throw new WatchPartyFailure(503, "Watch-party result not confirmed. Refresh before any further action. No automatic retry or rollback was performed.", "watchparty_uncertain");
        }
        finally
        {
            // A provider ignoring cancellation must not overlap another call. Observe its eventual
            // exception and keep the single bounded slot until it finishes, without blocking the host.
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(t => { _ = t.Exception; gate.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else gate.Release();
        }
    }
    private static WatchPartyState Project(WatchPartyInfo? party, Guid actor)
    {
        if (party is null) return new(true, false, null, false, 0, 0);
        if (party.PartyId == Guid.Empty || party.OwnerUserId == Guid.Empty || !Enum.IsDefined(party.Visibility) ||
            party.MaxViewers < 1 || party.ViewerCount < 1 || party.ViewerCount > party.MaxViewers)
            throw new WatchPartyFailure(503, "Private SyncPlay returned an unverifiable party snapshot.");
        // Do not return JoinCode, owner identity, participant/session IDs or the SDK object itself.
        return new(true, true, party.PartyId, party.OwnerUserId == actor, party.ViewerCount, party.MaxViewers);
    }
    private CreateInviteOptions InviteOptions()
    {
        var value = publicUrl();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new WatchPartyFailure(400, "An administrator must save the public HTTPS Jellyfin base URL first.");
        return new(url, AllowGuests: false, MaxUses: 1, ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30));
    }
    private static WatchPartyLink Link(WatchPartyInvite invite, Guid party, CreateInviteOptions options)
    {
        var url = invite.InviteUrl;
        var expected = options.ServerBaseUrl.AbsoluteUri.TrimEnd('/') + "/PrivateSyncPlay/Watch";
        if (invite.InviteId == Guid.Empty || invite.PartyId != party || invite.AllowGuests || invite.MaxUses != 1 ||
            invite.ExpiresAt is null || invite.ExpiresAt <= DateTimeOffset.UtcNow || invite.ExpiresAt > options.ExpiresAt ||
            url is null || !url.IsAbsoluteUri || url.GetLeftPart(UriPartial.Path) != expected || url.Query.Length != 0 || url.UserInfo.Length != 0 ||
            url.Fragment.Length is < 17 or > 2048 || url.AbsoluteUri.Length > 4096 || url.AbsoluteUri.Any(char.IsControl))
            throw new WatchPartyFailure(503, "Private SyncPlay returned an unverifiable invitation. Check the companion's party before taking further action.");
        return new(invite.InviteId, invite.PartyId, url.AbsoluteUri, invite.ExpiresAt.Value);
    }
    private async Task<WatchPartyInfo> CurrentParty(SessionInfo actor, Guid expected, bool owner, CancellationToken token)
    {
        var party = await provider.GetCurrentPartyAsync(actor.UserId, actor.Id, token);
        var state = Project(party, actor.UserId);
        if (!state.Active || state.PartyId != expected) throw new WatchPartyFailure(409, "Your player's party changed. Refresh before acting.");
        if (owner && !state.Owner) throw new WatchPartyFailure(403, "Only this party's owner in its active player can do that.");
        return party!;
    }
    public Task<WatchPartyState> Current(SessionInfo actor, CancellationToken cancellation) => Run(actor, async token =>
        Project(await provider.GetCurrentPartyAsync(actor.UserId, actor.Id, token), actor.UserId), cancellation);

    private async Task<WatchPartyManagement> OwnerSnapshot(SessionInfo actor, Guid party, CancellationToken token)
    {
        await CurrentParty(actor, party, true, token);
        var snapshot = await provider.GetManagementAsync(actor.UserId, actor.Id, party, token);
        if (snapshot is null || snapshot.PartyId != party || snapshot.Policy is null || snapshot.Policy.MaxViewers is < 1 or > 10000 || snapshot.ViewerCount < 1 || snapshot.ViewerCount > snapshot.Policy.MaxViewers ||
            snapshot.Accounts is null || snapshot.Guests is null || snapshot.BlockedAccounts is null || snapshot.Accounts.Count > 200 || snapshot.Guests.Count > 200 || snapshot.BlockedAccounts.Count > 1000 ||
            snapshot.Accounts.Count(a => a.IsOwner) != 1 || snapshot.Accounts.Any(a => a is null || a.ParticipantId == Guid.Empty || a.SessionCount < 1 || !SafeName(a.Name) || a.Permissions is null || a.EffectivePermissions is null) ||
            snapshot.Guests.Any(g => g is null || g.ParticipantId == Guid.Empty || !SafeName(g.Name) || g.SharedInviteGuestCount < 1) ||
            snapshot.BlockedAccounts.Any(a => a is null || a.ParticipantId == Guid.Empty || !SafeName(a.Name))) throw new WatchPartyFailure(503, "Owner management snapshot could not be verified.");
        var ids = snapshot.Accounts.Select(a => a.ParticipantId).Concat(snapshot.Guests.Select(g => g.ParticipantId)).Concat(snapshot.BlockedAccounts.Select(a => a.ParticipantId)).ToArray();
        if (ids.Length != ids.Distinct().Count()) throw new WatchPartyFailure(503, "Ambiguous party participant identity.");
        Actor(actor); return snapshot;
    }
    private static bool SafeName(string name) => name is not null && name.Length is > 0 and <= 200 && !name.Any(char.IsControl);
    private WatchPartyOwnerView OwnerView(WatchPartyManagement snapshot)
    {
        var value = new WatchPartyOwnerView(snapshot.PartyId, snapshot.Policy, snapshot.ViewerCount, snapshot.Accounts.ToArray(), snapshot.Guests.ToArray(), snapshot.BlockedAccounts.ToArray(), "");
        return value with { Revision = signReview(JsonSerializer.Serialize(value)) };
    }
    private async Task<WatchPartyManagement> ReviewedOwner(SessionInfo actor, Guid party, string revision, CancellationToken token)
    {
        var snapshot = await OwnerSnapshot(actor, party, token);
        if (revision is null || revision.Length != 64 || OwnerView(snapshot).Revision != revision) throw new WatchPartyFailure(409, "Party policy or participants changed. Reload owner controls and review again.");
        return snapshot;
    }
    public Task<WatchPartyOwnerView> Management(SessionInfo actor, Guid party, CancellationToken cancellation) => Run(actor, async token => OwnerView(await OwnerSnapshot(actor, party, token)), cancellation);
    public Task<bool> UpdatePolicy(SessionInfo actor, Guid party, WatchPartyPolicyChange update, CancellationToken cancellation) => Run(actor, async token =>
    {
        var snapshot = await ReviewedOwner(actor, party, update.Revision, token);
        if (update.MaxViewers < snapshot.ViewerCount || update.MaxViewers is < 1 or > 10000 || update.EditQueue && !update.ControlPlayback) throw new WatchPartyFailure(400, "Capacity must cover current viewers, and queue editing needs playback control.");
        Actor(actor);
        await provider.UpdatePolicyAsync(actor.UserId, actor.Id, party, snapshot.Policy with { AllowJoins = update.AllowJoins, AllowGuests = update.DisableGuests ? false : snapshot.Policy.AllowGuests,
            ParticipantsControlPlayback = update.ControlPlayback, ParticipantsEditQueue = update.EditQueue, MaxViewers = update.MaxViewers }, token);
        return true;
    }, cancellation);
    public Task<bool> AccountPermissions(SessionInfo actor, Guid party, Guid participant, string revision, bool playback, bool queue, CancellationToken cancellation) => Run(actor, async token =>
    {
        var snapshot = await ReviewedOwner(actor, party, revision, token);
        if (!snapshot.Accounts.Any(a => a.ParticipantId == participant && !a.IsOwner) || queue && !playback) throw new WatchPartyFailure(400, "Choose a current non-owner account; queue editing needs playback control.");
        Actor(actor); await provider.SetAccountPermissionsAsync(actor.UserId, actor.Id, party, participant, new(playback, queue), token); return true;
    }, cancellation);
    public Task<bool> Moderate(SessionInfo actor, Guid party, Guid participant, string revision, string action, bool confirmSharedGuests, CancellationToken cancellation) => Run(actor, async token =>
    {
        var snapshot = await ReviewedOwner(actor, party, revision, token); Actor(actor);
        if (action == "kick-account" && snapshot.Accounts.Any(a => a.ParticipantId == participant && !a.IsOwner)) await provider.KickAccountAsync(actor.UserId, actor.Id, party, participant, token);
        else if (action == "unblock-account" && snapshot.BlockedAccounts.Any(a => a.ParticipantId == participant)) await provider.UnblockAccountAsync(actor.UserId, actor.Id, party, participant, token);
        else if (action == "revoke-guest-link" && confirmSharedGuests && snapshot.Guests.Any(g => g.ParticipantId == participant)) await provider.KickGuestAndRevokeInviteAsync(actor.UserId, actor.Id, party, participant, token);
        else throw new WatchPartyFailure(400, "Participant or action changed. Guest-link removal requires explicit confirmation of its shared-link effect.");
        return true;
    }, cancellation);

    public Task<WatchPartyCreated> Create(SessionInfo actor, string name, CancellationToken cancellation) => Run(actor, async token =>
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl)) throw new WatchPartyFailure(400, "Use a party name of 1–80 characters.");
        var options = InviteOptions();
        if (await provider.GetCurrentPartyAsync(actor.UserId, actor.Id, token) is not null) throw new WatchPartyFailure(409, "This player already has a party. Refresh rather than creating another.");
        Actor(actor);
        var created = await provider.CreatePartyWithInviteAsync(actor.UserId, actor.Id, new(name.Trim(), WatchPartyVisibility.Private), options, token);
        var state = Project(created.Party, actor.UserId);
        if (!state.Owner || created.Party.Visibility != WatchPartyVisibility.Private) throw new WatchPartyFailure(503, "Private party creation could not be verified. Check the companion's current party.");
        return new WatchPartyCreated(state, Link(created.Invite, created.Party.PartyId, options));
    }, cancellation);

    public Task<WatchPartyLink> Invite(SessionInfo actor, Guid party, CancellationToken cancellation) => Run(actor, async token =>
    {
        await CurrentParty(actor, party, true, token); var options = InviteOptions(); Actor(actor);
        return Link(await provider.CreateInviteAsync(actor.UserId, actor.Id, party, options, token), party, options);
    }, cancellation);

    public Task<bool> Revoke(SessionInfo actor, Guid party, Guid invite, CancellationToken cancellation) => Run(actor, async token =>
    {
        if (invite == Guid.Empty) throw new WatchPartyFailure(400, "Select an invitation to revoke.");
        await CurrentParty(actor, party, true, token); Actor(actor);
        await provider.RevokeInviteAsync(actor.UserId, actor.Id, party, invite, token); return true;
    }, cancellation);

    public Task<bool> Leave(SessionInfo actor, Guid party, CancellationToken cancellation) => Run(actor, async token =>
    {
        await CurrentParty(actor, party, false, token); Actor(actor);
        await provider.LeavePartyAsync(actor.UserId, actor.Id, token); return true;
    }, cancellation);

    public Task<bool> End(SessionInfo actor, Guid party, CancellationToken cancellation) => Run(actor, async token =>
    {
        await CurrentParty(actor, party, true, token); Actor(actor); // No administrator ownership bypass.
        await provider.EndPartyAsync(actor.UserId, actor.Id, party, token); return true;
    }, cancellation);
}

public sealed record WatchPartyAvailability(bool Available, bool Active, string Code, string Message);
public sealed record WatchPartyState(bool Available, bool Active, Guid? PartyId, bool Owner, int ViewerCount, int MaxViewers);
public sealed record WatchPartyLink(Guid InviteId, Guid PartyId, string Url, DateTimeOffset ExpiresAt);
public sealed record WatchPartyCreated(WatchPartyState Party, WatchPartyLink Invite);
public sealed record WatchPartyOwnerView(Guid PartyId, WatchPartyPolicy Policy, int ViewerCount, IReadOnlyList<WatchPartyAccountParticipant> Accounts, IReadOnlyList<WatchPartyGuestParticipant> Guests, IReadOnlyList<WatchPartyBlockedAccount> BlockedAccounts, string Revision);
public sealed record WatchPartyPolicyChange(string Revision, bool AllowJoins, bool ControlPlayback, bool EditQueue, int MaxViewers, bool DisableGuests = false);
public sealed class WatchPartyFailure(int status, string message, string code = "watchparty_error") : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
