using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.MediaHub;

public sealed record PlayerDestination(string Id, string Name, string Client);
public sealed record PlaybackPreview(string Id, string Title, string Device, DateTimeOffset Expires);
public sealed record PlaybackReceipt(string Id, string State, bool? ObservedPlayingItem);
public sealed record PlaybackHistory(PlaybackPreview Preview, PlaybackReceipt Receipt);

/// <summary>Only a user's existing real players; explicit once-only native commands, never playback reports or synthetic sessions.</summary>
public sealed class NativePlayback(HubStore store, SettingsService settings, HostIdentity identity, ISessionManager sessions, ILibraryManager library, INativePlaybackGuard? guard = null)
{
    private sealed record Plan(string Actor, string Target, string Device, Guid Item, string Title, string Label, int Configuration, DateTimeOffset Expires);
    private readonly SemaphoreSlim slot = new(1);
    private void Actor(SessionInfo actor)
    {
        var user = identity.Active(actor.UserId);
        if (!user.HasPermission(PermissionKind.EnableMediaPlayback) || !SafeId(actor.Id, 128) || !SafeId(actor.DeviceId, 256) ||
            sessions.Sessions.Count(s => s.Id == actor.Id && s.UserId == actor.UserId && s.DeviceId == actor.DeviceId) != 1) throw new HubError("Your current authenticated Jellyfin player and playback permission are required.");
    }
    private static bool SafeId(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool RealPlayer(SessionInfo target) => target.IsActive && target.SupportsMediaControl && target.SupportsRemoteControl && SafeId(target.Id, 128) && SafeId(target.DeviceId, 256);
    public IReadOnlyList<PlayerDestination> Destinations(SessionInfo actor)
    {
        Actor(actor); var rows = sessions.Sessions.Where(s => s.UserId == actor.UserId && RealPlayer(s)).Take(51).ToArray();
        if (rows.Length > 50 || rows.Select(s => s.Id).Distinct().Count() != rows.Length) throw new HubError("Player scan is incomplete or ambiguous.");
        return rows.Select(s => new PlayerDestination(s.Id, Clip(s.DeviceName), Clip(s.Client))).ToArray();
    }
    private static string Clip(string? value) => new((value ?? "Jellyfin player").Where(c => !char.IsControl(c)).Take(160).ToArray());
    private SessionInfo Target(SessionInfo actor, string target, string? device = null)
    {
        Actor(actor); var rows = sessions.Sessions.Where(s => s.Id == target && s.UserId == actor.UserId && (device is null || s.DeviceId == device) && RealPlayer(s)).Take(2).ToArray();
        return rows.Length == 1 ? rows[0] : throw new HubError("That player disconnected, changed accounts or no longer permits remote control.");
    }
    private BaseItem Item(SessionInfo actor, SessionInfo target, Guid id)
    {
        var item = library.GetItemById(id);
        if (item is null || item.IsFolder || item is not (Video or MediaBrowser.Controller.Entities.Audio.Audio) || !item.IsVisibleStandalone(identity.Active(actor.UserId)) || !target.PlayableMediaTypes.Contains(item.MediaType)) throw new HubError("Choose an accessible playable movie, episode or audio item supported by that player. Open folders/series in Jellyfin first.");
        return item;
    }
    private static string Purpose(string id, Guid user, string state) => $"MediaHub.NativePlayback.v1|{id}|{user:N}|{state}";
    private Plan Decode(Guid user, string id, string state, string payload)
    {
        if (state is not ("prepared" or "sending" or "accepted" or "uncertain" or "cancelled" or "reviewed") || payload.Length > 8192) throw new HubError("Invalid retained playback state. No record was reset.");
        return SafeJson.Read<Plan>(settings.Unprotect(payload, Purpose(id, user, state)), 4096);
    }
    private (string State, string Payload, Plan Plan) Read(Guid user, string id)
    {
        if (id is null || id.Length != 48 || !id.All(Uri.IsHexDigit)) throw new HubError("Playback receipt unavailable.");
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT state,payload FROM playback_operations WHERE id=$0 AND user=$1", id, user.ToString("N")); using var r = read.ExecuteReader();
        if (!r.Read()) throw new HubError("Playback receipt unavailable."); var state = r.GetString(0); var payload = r.GetString(1);
        return (state, payload, Decode(user, id, state, payload));
    }
    public PlaybackPreview Prepare(SessionInfo actor, string target, Guid item)
    {
        var player = Target(actor, target); var media = Item(actor, player, item); var id = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var plan = new Plan(actor.Id, player.Id, player.DeviceId, media.Id, Clip(media.Name), Clip(player.DeviceName), settings.Current.Revision, expires);
        using var db = store.Open(); using var tx = db.BeginTransaction(); using var count = HubStore.Command(db, "SELECT COUNT(*) FROM playback_operations"); count.Transaction = tx;
        if ((long)count.ExecuteScalar()! >= 5000) throw new HubError("Playback receipt capacity reached. Existing receipts were retained.");
        var unresolved = new List<(string Id, string State, string Payload)>(); using (var read = HubStore.Command(db, "SELECT id,state,CASE WHEN LENGTH(payload)<=8192 THEN payload ELSE NULL END FROM playback_operations WHERE user=$0 LIMIT 501", actor.UserId.ToString("N")))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) { if (r.IsDBNull(2)) throw new HubError("Retained playback proof exceeds its safe budget."); unresolved.Add((r.GetString(0), r.GetString(1), r.GetString(2))); } }
        if (unresolved.Count >= 500) throw new HubError("Your 500 retained player receipts reached capacity. Existing receipts were preserved.");
        var pending = 0;
        foreach (var previous in unresolved)
        {
            var previousPlan = Decode(actor.UserId, previous.Id, previous.State, previous.Payload);
            if (previous.State is not ("prepared" or "sending" or "uncertain")) continue;
            if (previous.State == "prepared" && previousPlan.Expires <= DateTimeOffset.UtcNow) continue;
            pending++;
            if (previousPlan.Target == player.Id && previousPlan.Item == media.Id) throw new HubError("This item already has a pending player command. Inspect/cancel its preview or explicitly acknowledge the uncertain receipt before preparing another.");
        }
        if (pending >= 20) throw new HubError("Too many pending player previews. Review existing receipts first.");
        using var add = HubStore.Command(db, "INSERT INTO playback_operations VALUES($0,$1,'prepared',$2)", id, actor.UserId.ToString("N"), settings.Protect(JsonSerializer.Serialize(plan), Purpose(id, actor.UserId, "prepared"))); add.Transaction = tx; add.ExecuteNonQuery(); tx.Commit();
        return new(id, plan.Title, plan.Label, expires);
    }
    public PlaybackReceipt Receipt(SessionInfo actor, string id)
    {
        Actor(actor); var row = Read(actor.UserId, id); var target = sessions.Sessions.SingleOrDefault(s => s.Id == row.Plan.Target && s.UserId == actor.UserId && s.DeviceId == row.Plan.Device);
        var item = library.GetItemById(row.Plan.Item); if (item is null || !item.IsVisibleStandalone(identity.Active(actor.UserId))) throw new HubError("Playback item no longer accessible.");
        return new(id, row.State, target is null ? null : target.NowPlayingItem?.Id == row.Plan.Item); // Observation, not attribution or confirmation of this command.
    }
    public IReadOnlyList<PlaybackHistory> History(SessionInfo actor)
    {
        Actor(actor); var ids = new List<string>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT id FROM playback_operations WHERE user=$0 ORDER BY CASE WHEN state IN ('sending','uncertain') THEN 0 ELSE 1 END,rowid DESC LIMIT 50", actor.UserId.ToString("N")))
        using (var r = read.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        var result = new List<PlaybackHistory>();
        foreach (var id in ids)
        {
            var row = Read(actor.UserId, id); var item = library.GetItemById(row.Plan.Item);
            if (item is null || !item.IsVisibleStandalone(identity.Active(actor.UserId))) continue;
            result.Add(new(new(id, Clip(item.Name), row.Plan.Label, row.Plan.Expires), Receipt(actor, id)));
        }
        return result;
    }
    private bool Transition(Guid user, string id, string before, string payload, string state, Plan plan)
    {
        using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE playback_operations SET state=$0,payload=$1 WHERE id=$2 AND user=$3 AND state=$4 AND payload=$5", state, settings.Protect(JsonSerializer.Serialize(plan), Purpose(id, user, state)), id, user.ToString("N"), before, payload); return update.ExecuteNonQuery() == 1;
    }
    public PlaybackReceipt Cancel(SessionInfo actor, string id)
    {
        Actor(actor); var row = Read(actor.UserId, id);
        if (row.State != "prepared" || !Transition(actor.UserId, id, row.State, row.Payload, "cancelled", row.Plan)) throw new HubError("Only an unsent playback preview can be cancelled.");
        return Receipt(actor, id);
    }
    public PlaybackReceipt Acknowledge(SessionInfo actor, string id)
    {
        Actor(actor); var row = Read(actor.UserId, id); _ = Receipt(actor, id);
        if (slot.CurrentCount == 0 || row.State is not ("sending" or "uncertain") || !Transition(actor.UserId, id, row.State, row.Payload, "reviewed", row.Plan)) throw new HubError("Wait for any pending player operation to finish. Only an uncertain receipt can be acknowledged.");
        return Receipt(actor, id); // Records manual inspection only; no retry, cancellation, or claim that the command failed.
    }
    public async Task<PlaybackReceipt> Confirm(SessionInfo actor, string id, Func<Task> verifyActor, CancellationToken cancellation)
    {
        if (!await slot.WaitAsync(0, cancellation)) throw new HubError("A native player command is still pending. Read its receipt; do not create a replacement.");
        Task? pending = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var user = actor.UserId; var actorId = actor.Id; var actorDevice = actor.DeviceId;
            Actor(actor); var row = Read(actor.UserId, id); if (row.State != "prepared") return Receipt(actor, id);
            if (row.Plan.Actor != actor.Id || row.Plan.Expires <= DateTimeOffset.UtcNow || row.Plan.Configuration != settings.Current.Revision) throw new HubError("Playback preview expired or its actor/configuration changed.");
            // Playback is an integration feature, not a standalone deletion operation.
            // A missing/unhealthy integration cannot silently bypass group authority.
            if (guard is null) throw new HubError("Discord Integration is required for these player controls.");
            pending = Task.Run(() => guard.Check(user, actorId, row.Plan.Target, budget.Token), CancellationToken.None);
            await pending.WaitAsync(budget.Token);
            var target = Target(actor, row.Plan.Target, row.Plan.Device); _ = Item(actor, target, row.Plan.Item); await verifyActor(); Actor(actor); budget.Token.ThrowIfCancellationRequested();
            if (actor.UserId != user || actor.Id != actorId || actor.DeviceId != actorDevice) throw new HubError("Your native actor changed. No command was sent.");
            if (!Transition(actor.UserId, id, "prepared", row.Payload, "sending", row.Plan)) return Receipt(actor, id);
            var sending = Read(actor.UserId, id); var result = "uncertain";
            try
            {
                var token = budget.Token;
                pending = Task.Run(async () =>
                {
                    if (actor.UserId != user || actor.Id != actorId || actor.DeviceId != actorDevice) throw new HubError("Your native actor changed.");
                    var live = Target(actor, row.Plan.Target, row.Plan.Device); _ = Item(actor, live, row.Plan.Item);
                    await sessions.SendPlayCommand(actorId, live.Id, new PlayRequest { ItemIds = [row.Plan.Item], PlayCommand = PlayCommand.PlayNow, ControllingUserId = user }, token);
                }, CancellationToken.None);
                await pending.WaitAsync(budget.Token); result = "accepted";
            }
            catch { /* A command may already have reached the player. Never replay it or fabricate a playback event. */ }
            Transition(user, id, "sending", sending.Payload, result, row.Plan); await verifyActor(); return Receipt(actor, id);
        }
        finally
        {
            if (pending is { IsCompleted: false }) _ = pending.ContinueWith(t => { _ = t.Exception; slot.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else slot.Release();
        }
    }
}
