using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record QueueItem(string Id, Guid Item, string Title, string State, DateTimeOffset? DueAt, long Revision, string Owner, bool CanUndo, bool AutomaticDeletion = false);
public sealed record ConsentNotice(string Id, string Proposal, Guid Item, string Title, string Message);
public sealed record DeletionAudit(string Id, Guid Owner, int RequestId, string Target, string State, DateTimeOffset Created, long Revision);
internal sealed record QueuedDeletion(string Id, Guid Owner, Guid Item, int Request, string Target, string State,
    DateTimeOffset? Eligible, DateTimeOffset? Due, long Revision, DateTimeOffset Created, int Delay);

/// <summary>Shared proposals, consent and checkpointed Arr execution. Never operates on media files directly.</summary>
public sealed partial class DeletionQueue(HubStore store, SettingsService settings, PersonalService personal,
    IViewerMedia media, IRecentActivity activity, IRequestAuthority authority, IArrDeletion? backend = null)
{
    private readonly SemaphoreSlim gate = new(1);
    private static string Key(Guid id) => id.ToString("N");
    private static string ProofPurpose(string id, Guid owner, Guid item, string state) => string.Join('|', "MediaHub.Deletion.v1", id, Key(owner), Key(item), state);
    private static string? Time(DateTimeOffset? value) => value?.ToString("O");
    private static DateTimeOffset? Parse(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i));
    private QueuedDeletion Get(string id)
    {
        using var db = store.Open(); return Get(db, id);
    }
    private static QueuedDeletion Get(SqliteConnection db, string id, SqliteTransaction? tx = null)
    {
        using var command = HubStore.Command(db, "SELECT * FROM deletion_queue WHERE id=$0", id); command.Transaction = tx;
        using var r = command.ExecuteReader(); if (!r.Read()) throw new HubError("Proposal unavailable.");
        return new(r.GetString(0), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetInt32(3), r.GetString(4), r.GetString(5),
            Parse(r, 6), Parse(r, 7), r.GetInt64(8), DateTimeOffset.Parse(r.GetString(9)), r.GetInt32(10));
    }
    public IReadOnlyList<QueueItem> List(User viewer)
    {
        var result = new List<QueueItem>();
        foreach (var id in Ids())
        {
            var row = Get(id); var visible = media.Find(viewer, row.Item); if (visible is null) continue;
            var mine = row.Owner == viewer.Id;
            var state = row.State is "waiting" or "queued" or "ready" or "blocked" or "executing" or "checking" or "cleanup" or "uncertain" ? row.State : "blocked";
            result.Add(new(row.Id, row.Item, visible.Title, state, row.Due, row.Revision, mine ? "Yours" : "Anonymous", mine && row.State is ("waiting" or "queued" or "ready" or "blocked"), settings.Current.AutomaticDeletion));
        }
        return result;
    }
    public DeletionAudit Audit(User viewer, string id)
    {
        if (!viewer.HasPermission(PermissionKind.IsAdministrator)) throw new HubError("Administrator access required.");
        var row = Get(id); return new(row.Id, row.Owner, row.Request, row.Target, row.State, row.Created, row.Revision);
    }
    private List<string> Ids(Guid? owner = null)
    {
        using var db = store.Open(); using var cmd = HubStore.Command(db, "SELECT id FROM deletion_queue WHERE state NOT IN ('cancelled','completed') AND ($0 IS NULL OR owner=$0) ORDER BY created LIMIT 500", owner is null ? null : Key(owner.Value));
        using var r = cmd.ExecuteReader(); var ids = new List<string>(); while (r.Read()) ids.Add(r.GetString(0)); return ids;
    }
    public IReadOnlyList<Guid> Owners()
    {
        using var db = store.Open(); using var command = HubStore.Command(db, "SELECT DISTINCT owner FROM deletion_queue WHERE state NOT IN ('cancelled','completed') LIMIT 500");
        using var r = command.ExecuteReader(); var result = new List<Guid>(); while (r.Read()) result.Add(Guid.Parse(r.GetString(0))); return result;
    }
    public void Block(Guid owner)
    {
        foreach (var id in Ids(owner)) BlockProposal(id);
    }
    public async Task Propose(User viewer, int request, Guid item, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (Ids().Count >= 500) throw new HubError("Deletion queue is full. Try later.");
            var target = await authority.Verify(viewer, request, item, cancellation);
            if (target.Item != item || media.Find(viewer, item) is null) throw new HubError("Item unavailable.");
            var proof = backend is null ? null : await backend.Capture(viewer, target, cancellation);
            var viewers = activity.Read(item); var now = DateTimeOffset.UtcNow;
            var delay = personal.State(viewer).EffectiveDeletionDelayDays;
            using (var db = store.Open())
            using (var tx = db.BeginTransaction())
            {
                using var duplicate = HubStore.Command(db, "SELECT 1 FROM deletion_queue WHERE (target=$0 OR (item=$1 AND (request_id=0 OR $2=0))) AND state NOT IN ('cancelled','completed')", target.Pin, Key(item), request); duplicate.Transaction = tx;
                if (duplicate.ExecuteScalar() is not null) throw new HubError("Deletion is already proposed.");
                var id = HubStore.Nonce();
                using var write = HubStore.Command(db, "INSERT INTO deletion_queue VALUES($0,$1,$2,$3,$4,'waiting',NULL,NULL,0,$5,$6)", id, Key(viewer.Id), Key(item), request, target.Pin, Time(now), delay);
                write.Transaction = tx; write.ExecuteNonQuery();
                if (proof is not null)
                {
                    using var intent = HubStore.Command(db, "INSERT INTO deletion_runs VALUES($0,$1,'prepared',$2)", id,
                        settings.ProtectDeletionProof(System.Text.Json.JsonSerializer.Serialize(proof), ProofPurpose(id, viewer.Id, item, "prepared")), Time(now));
                    intent.Transaction = tx; intent.ExecuteNonQuery();
                }
                foreach (var own in viewers.Where(v => v.UserId == viewer.Id)) Vote(db, tx, id, viewer.Id, own.LastActivity, true);
                SealQueue(db, id, tx, "proposed", viewer.Id, title: media.Find(viewer, item)?.Title ?? throw new HubError("Item access changed before the proposal was saved."));
                tx.Commit();
            }
        }
        finally { gate.Release(); }
        await Refresh(viewer, cancellation);
    }
    private static void Vote(SqliteConnection db, SqliteTransaction tx, string id, Guid user, DateTimeOffset at, bool approve)
    {
        using var command = HubStore.Command(db, "INSERT INTO deletion_votes VALUES($0,$1,$2,$3) ON CONFLICT(proposal,user) DO UPDATE SET activity=excluded.activity,approved=excluded.approved",
            id, Key(user), Time(at), approve ? 1 : 0); command.Transaction = tx; command.ExecuteNonQuery();
    }
    public async Task Refresh(User viewer, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            foreach (var id in Ids(viewer.Id))
            {
                var row = Get(id); if (row.Owner != viewer.Id) continue;
                if (row.State is not ("waiting" or "queued" or "ready" or "blocked")) continue;
                using (var check = store.Open()) { try { CheckAuthorization(check, row); } catch (HubError) { BlockProposal(id); continue; } }
                var config = settings.Current; var rules = new DeletionRules(config.RecentActivityDays, config.DeletionDelayDays);
                var now = DateTimeOffset.UtcNow; IReadOnlyList<RecentViewer> viewers = [];
                DeletionDecision decision;
                string? reason = null;
                var delay = Math.Clamp(Math.Max(row.Delay, personal.State(viewer).EffectiveDeletionDelayDays), rules.DefaultDelayDays, rules.RecentActivityDays);
                try
                {
                    var target = await authority.Verify(viewer, row.Request, row.Item, cancellation);
                    if (target.Pin != row.Target || target.Item != row.Item) throw new HubError("Target changed.");
                    viewers = activity.Read(row.Item);
                    now = DateTimeOffset.UtcNow;
                    using var read = store.Open(); using var votes = HubStore.Command(read, "SELECT user,activity FROM deletion_votes WHERE proposal=$0 AND approved=1", id);
                    using var r = votes.ExecuteReader(); var approvals = new List<DeletionConsent>();
                    while (r.Read()) approvals.Add(new(Guid.Parse(r.GetString(0)), DateTimeOffset.Parse(r.GetString(1))));
                    decision = DeletionPolicy.Evaluate(rules, now, viewer.Id, row.Owner, true, true, viewers, approvals,
                        delay, row.Eligible, row.Due);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) { decision = new(DeletionStage.Unavailable); reason = error is HubError { Code: "deletion_denied" } ? "permission_revoked" : "target_or_activity_unavailable"; }
                using var db = store.Open(); using var tx = db.BeginTransaction();
                CheckAuthorization(db, row, tx);
                var state = decision.Stage switch { DeletionStage.AwaitingConsent => "waiting", DeletionStage.Queued => "queued", DeletionStage.Ready => "ready", _ => "blocked" };
                var noticesAdded = 0;
                foreach (var watcher in viewers.Where(v => v.LastActivity >= now.AddDays(-rules.RecentActivityDays)))
                {
                    using var notice = HubStore.Command(db, "INSERT OR IGNORE INTO deletion_notices VALUES($0,$1,$2,$3)", HubStore.Nonce(), Key(watcher.UserId), id, Time(watcher.LastActivity));
                    notice.Transaction = tx; noticesAdded += notice.ExecuteNonQuery();
                }
                if (state == row.State && decision.EligibleSince == row.Eligible && decision.DueAt == row.Due && delay == row.Delay && noticesAdded == 0)
                { tx.Commit(); continue; } // Polling is not a new audit event or revision.
                using var update = HubStore.Command(db, "UPDATE deletion_queue SET state=$0,eligible=$1,due=$2,revision=revision+1,delay=MAX(delay,$5) WHERE id=$3 AND revision=$4",
                    state, Time(decision.EligibleSince), Time(decision.DueAt), id, row.Revision, delay); update.Transaction = tx;
                if (update.ExecuteNonQuery() != 1) throw new HubError("Proposal changed. Refresh.");
                SealQueue(db, id, tx, state == "blocked" ? "blocked" : "refreshed", reason: reason); tx.Commit();
            }
        }
        finally { gate.Release(); }
    }
    public IReadOnlyList<ConsentNotice> Notices(User viewer)
    {
        var preferences = personal.State(viewer).Preferences;
        using var db = store.Open(); using var cmd = HubStore.Command(db, """
            SELECT n.id,n.proposal,q.item,q.delay FROM deletion_notices n JOIN deletion_queue q ON q.id=n.proposal
            WHERE n.user=$0 AND q.state IN ('waiting','blocked','queued','ready')
            AND n.activity>=$1
            AND n.activity=(SELECT MAX(newer.activity) FROM deletion_notices newer WHERE newer.user=n.user AND newer.proposal=n.proposal)
            AND NOT EXISTS(SELECT 1 FROM deletion_votes v WHERE v.proposal=n.proposal AND v.user=n.user AND v.activity=n.activity) LIMIT 200
            """, Key(viewer.Id), Time(DateTimeOffset.UtcNow.AddDays(-settings.Current.RecentActivityDays)));
        using var r = cmd.ExecuteReader(); var result = new List<ConsentNotice>();
        while (r.Read())
        {
            var item = Guid.Parse(r.GetString(2)); var visible = media.Find(viewer, item) ?? media.FindForConsent(viewer, item);
            if (visible is not null) result.Add(new(r.GetString(0), r.GetString(1), visible.Id, visible.Title,
                DeletionMessages.Render(preferences.DeletionMessage, visible.Title, Math.Max(r.GetInt32(3), settings.Current.DeletionDelayDays), settings.Current.RecentActivityDays)));
        }
        return result;
    }
    public async Task Respond(User viewer, string notice, bool approve, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            using var db = store.Open(); using var read = HubStore.Command(db, "SELECT proposal,activity FROM deletion_notices WHERE id=$0 AND user=$1", notice, Key(viewer.Id));
            using var r = read.ExecuteReader(); if (!r.Read()) throw new HubError("Notification unavailable.");
            var id = r.GetString(0); var at = DateTimeOffset.Parse(r.GetString(1)); r.Close();
            var row = Get(id);
            if (row.State is not ("waiting" or "blocked" or "queued" or "ready") || (media.Find(viewer, row.Item) ?? media.FindForConsent(viewer, row.Item)) is null) throw new HubError("Proposal unavailable.");
            var latest = activity.Read(row.Item).Where(v => v.UserId == viewer.Id).MaxBy(v => v.LastActivity);
            if (latest is null || latest.LastActivity != at) throw new HubError("Activity changed. Refresh notifications.");
            CheckAuthorization(db, row);
            using var previous = HubStore.Command(db, "SELECT approved FROM deletion_votes WHERE proposal=$0 AND user=$1 AND activity=$2", id, Key(viewer.Id), Time(at));
            if (previous.ExecuteScalar() is long saved && saved == (approve ? 1 : 0)) return;
            using var tx = db.BeginTransaction(); CheckAuthorization(db, row, tx); Vote(db, tx, id, viewer.Id, at, approve);
            // Any response invalidates the old deadline until the owner's full live recheck.
            using var reset = HubStore.Command(db, "UPDATE deletion_queue SET state='waiting',eligible=NULL,due=NULL,revision=revision+1 WHERE id=$0 AND revision=$1 AND state NOT IN ('cancelled','completed')", id, row.Revision);
            reset.Transaction = tx; if (reset.ExecuteNonQuery() != 1) throw new HubError("Proposal changed. Refresh."); SealQueue(db, id, tx, approve ? "consent_yes" : "consent_no", viewer.Id); tx.Commit();
        }
        finally { gate.Release(); }
    }
    public void Undo(User viewer, string id)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); var authenticated = HasAuthorization(db, Get(db, id, tx), tx);
        using var undo = HubStore.Command(db, "UPDATE deletion_queue SET state='cancelled',eligible=NULL,due=NULL,revision=revision+1 WHERE id=$0 AND owner=$1 AND state IN ('waiting','queued','ready','blocked')", id, Key(viewer.Id)); undo.Transaction = tx;
        if (undo.ExecuteNonQuery() != 1) throw new HubError("Only the requester can undo this proposal.");
        if (authenticated) SealQueue(db, id, tx, "cancelled", viewer.Id); tx.Commit();
    }
}
