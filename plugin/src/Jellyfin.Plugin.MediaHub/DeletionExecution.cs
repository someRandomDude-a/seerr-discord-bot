using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DeletionQueue
{
    private void BlockProposal(string id)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); var row = Get(db, id, tx); var authenticated = HasAuthorization(db, row, tx);
        if (row.State == "blocked" && row.Eligible is null && row.Due is null) { tx.Commit(); return; }
        using var update = HubStore.Command(db, "UPDATE deletion_queue SET state='blocked',eligible=NULL,due=NULL,revision=revision+1 WHERE id=$0 AND state IN ('waiting','queued','ready','blocked')", id); update.Transaction = tx;
        if (update.ExecuteNonQuery() == 1 && authenticated) SealQueue(db, id, tx, "blocked", reason: "verification_unavailable"); tx.Commit();
    }
    private (ArrProof Proof, string State)? Run(QueuedDeletion row)
    {
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT proof,state FROM deletion_runs WHERE proposal=$0", row.Id);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) return null;
        var state = reader.GetString(1);
        return (SafeJson.Read<ArrProof>(settings.UnprotectDeletionProof(reader.GetString(0), ProofPurpose(row.Id, row.Owner, row.Item, state)), SettingsService.DeletionProofLimit), state);
    }
    private void Checkpoint(string id, string run, string queue)
    {
        var row = Get(id); var previous = Run(row) ?? throw new HubError("Deletion checkpoint proof is missing.");
        if ((previous.State, run) is not (("sending", "checking") or ("sending", "uncertain") or ("uncertain", "uncertain") or ("uncertain", "files_deleted") or ("checking", "files_deleted") or ("sending", "files_deleted") or ("files_deleted", "completed")))
            throw new HubError("Invalid deletion checkpoint transition.");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        CheckAuthorization(db, row, tx);
        using var write = HubStore.Command(db, "UPDATE deletion_runs SET state=$0,updated=$1,proof=$3 WHERE proposal=$2 AND state=$4", run, Time(DateTimeOffset.UtcNow), id,
            settings.ProtectDeletionProof(JsonSerializer.Serialize(previous.Proof), ProofPurpose(id, row.Owner, row.Item, run)), previous.State);
        write.Transaction = tx; if (write.ExecuteNonQuery() != 1) throw new HubError("Deletion checkpoint changed.");
        using var status = HubStore.Command(db, "UPDATE deletion_queue SET state=$0,revision=revision+1 WHERE id=$1", queue, id);
        status.Transaction = tx; status.ExecuteNonQuery(); SealQueue(db, id, tx, run); tx.Commit();
    }
    private bool Claim(QueuedDeletion row, ArrProof proof)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction();
        if (!HasAuthorization(db, row, tx)) return false;
        using var queue = HubStore.Command(db, "UPDATE deletion_queue SET state='executing',revision=revision+1 WHERE id=$0 AND owner=$1 AND state='ready' AND revision=$2", row.Id, Key(row.Owner), row.Revision);
        queue.Transaction = tx; if (queue.ExecuteNonQuery() != 1) return false;
        using var intent = HubStore.Command(db, "UPDATE deletion_runs SET state='sending',updated=$0,proof=$2 WHERE proposal=$1 AND state='prepared'", Time(DateTimeOffset.UtcNow), row.Id,
            settings.ProtectDeletionProof(JsonSerializer.Serialize(proof), ProofPurpose(row.Id, row.Owner, row.Item, "sending")));
        intent.Transaction = tx; if (intent.ExecuteNonQuery() != 1) return false;
        SealQueue(db, row.Id, tx, "deletion_claimed"); tx.Commit(); return true;
    }
    public async Task Execute(User owner, CancellationToken cancellation)
    {
        if (backend is null) return;
        await gate.WaitAsync(cancellation);
        try
        {
            foreach (var id in Ids(owner.Id))
            {
                var row = Get(id); (ArrProof Proof, string State)? saved;
                try { using var check = store.Open(); CheckAuthorization(check, row); }
                catch { BlockProposal(id); continue; } // Old/untrusted pre-send proposals remain owner-cancellable, never approved by a refresh.
                try { saved = Run(row); }
                catch
                {
                    using var db = store.Open(); using var quarantine = HubStore.Command(db, "UPDATE deletion_queue SET state='uncertain',eligible=NULL,due=NULL,revision=revision+1 WHERE id=$0 AND state NOT IN ('cancelled','completed')", id); quarantine.ExecuteNonQuery();
                    continue; // Unauthenticated legacy/tampered checkpoints can never authorize cleanup or another deletion.
                }
                // Older review-only proposals have no pinned Arr proof. Never infer one after their deadline.
                if (saved is null) { if (settings.Current.AutomaticDeletion && row.State == "ready") BlockProposal(id); continue; }
                var (proof, state) = saved.Value;
                if (state == "completed") continue;
                if (state == "prepared")
                {
                    if (!settings.Current.AutomaticDeletion || row.State != "ready") continue;
                    PreparedArr prepared;
                    try
                    {
                        var verified = await authority.Verify(owner, row.Request, row.Item, cancellation);
                        if (verified.Pin != row.Target || verified.Item != row.Item) throw new HubError("Target changed.");
                        if (verified.Arr is not null && verified.Arr != proof.Request) throw new HubError("Library target changed.");
                        prepared = await backend.Prepare(owner, proof, cancellation);
                        using var db = store.Open(); using var votes = HubStore.Command(db, "SELECT user,activity FROM deletion_votes WHERE proposal=$0 AND approved=1", id);
                        using var reader = votes.ExecuteReader(); var approvals = new List<DeletionConsent>();
                        while (reader.Read()) approvals.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1))));
                        var viewers = activity.Read(row.Item); var config = settings.Current;
                        var rules = new DeletionRules(config.RecentActivityDays, config.DeletionDelayDays);
                        var decision = DeletionPolicy.Evaluate(rules, DateTimeOffset.UtcNow, owner.Id, row.Owner, true, true, viewers, approvals,
                            Math.Clamp(Math.Max(row.Delay, personal.State(owner).EffectiveDeletionDelayDays), rules.DefaultDelayDays, rules.RecentActivityDays), row.Eligible, row.Due);
                        if (!config.AutomaticDeletion || decision.Stage != DeletionStage.Ready) { BlockProposal(id); continue; }
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch { BlockProposal(id); continue; }
                    if (!Claim(row, proof)) continue; // Durable authenticated intent precedes the single outgoing DELETE.
                    try
                    {
                        await backend.Delete(prepared, cancellation);
                        Checkpoint(id, "checking", "checking"); state = "checking";
                    }
                    catch
                    {
                        Checkpoint(id, "uncertain", "uncertain");
                        cancellation.ThrowIfCancellationRequested(); continue;
                    }
                }
                if (state is "sending" or "uncertain" or "checking")
                {
                    try
                    {
                        // Restart recovery is read-only. Never resend an uncertain/partially completed DELETE.
                        if (!await backend.Gone(owner, proof, cancellation))
                        { if (state != "checking") Checkpoint(id, "uncertain", "uncertain"); continue; }
                        Checkpoint(id, "files_deleted", "cleanup"); state = "files_deleted";
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch { continue; }
                }
                if (state == "files_deleted")
                {
                    try
                    {
                        await backend.Unmonitor(owner, proof, cancellation);
                        Checkpoint(id, "completed", "completed");
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch { /* Retain the checkpoint; retry only idempotent monitoring cleanup. */ }
                }
            }
        }
        finally { gate.Release(); }
    }
}
