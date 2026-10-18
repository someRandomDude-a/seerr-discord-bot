using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DeletionQueue
{
    private string AuthorizationData(SqliteConnection db, QueuedDeletion row, SqliteTransaction? tx, string? audit = null)
    {
        var votes = new List<(string User, string Activity, long Approved)>();
        using var read = HubStore.Command(db, "SELECT user,activity,approved FROM deletion_votes WHERE proposal=$0 ORDER BY user LIMIT 10001", row.Id); read.Transaction = tx;
        using (var r = read.ExecuteReader()) while (r.Read()) votes.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        if (votes.Count > 10000) throw new HubError("Proposal consent exceeds its supported size.");
        return audit is null ? JsonSerializer.Serialize(new { Proposal = row, Votes = votes.Select(v => new { v.User, v.Activity, v.Approved }) })
            : JsonSerializer.Serialize(new { Proposal = row, Votes = votes.Select(v => new { v.User, v.Activity, v.Approved }), Audit = audit });
    }
    private void CheckAuthorization(SqliteConnection db, QueuedDeletion row, SqliteTransaction? tx = null)
    {
        if (Get(db, row.Id, tx) != row) throw new HubError("Proposal changed. Refresh before acting.");
        using var read = HubStore.Command(db, "SELECT integrity FROM deletion_authorizations WHERE proposal=$0", row.Id); read.Transaction = tx;
        var audit = settings.Protection.CheckAudit(db, tx, row.Id);
        var signature = read.ExecuteScalar() as string;
        if (signature is null || audit is not null && !signature.StartsWith("audit1:", StringComparison.Ordinal) ||
            !settings.Authenticated("MediaHub.DeletionConsent.v1", AuthorizationData(db, row, tx, audit), audit is null ? signature : signature[7..]))
            throw new HubError("Proposal consent or deadline integrity could not be verified. Undo and explicitly propose again; no removal was authorized.");
    }
    internal void SealQueue(SqliteConnection db, string id, SqliteTransaction? tx = null, string kind = "snapshot", Guid? actor = null, string? reason = null, string? title = null)
    {
        if (tx is null) { using var owned = db.BeginTransaction(); SealQueue(db, id, owned, kind, actor, reason, title); owned.Commit(); return; }
        var row = Get(db, id, tx);
        AppendAudit(db, tx, row, kind, actor, reason, title);
        var audit = settings.Protection.SealAudit(db, tx, id);
        var signature = "audit1:" + settings.Authenticate("MediaHub.DeletionConsent.v1", AuthorizationData(db, row, tx, audit));
        using var write = HubStore.Command(db, "INSERT INTO deletion_authorizations VALUES($0,$1) ON CONFLICT(proposal) DO UPDATE SET integrity=excluded.integrity", id, signature); write.Transaction = tx; write.ExecuteNonQuery();
    }
    private bool HasAuthorization(SqliteConnection db, QueuedDeletion row, SqliteTransaction tx)
    {
        try { CheckAuthorization(db, row, tx); return true; }
        catch (HubError) { return false; } // May block/cancel untrusted old state, but must not manufacture a valid seal.
    }
}
