using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DeletionQueue
{
    private string AuthorizationData(SqliteConnection db, QueuedDeletion row, SqliteTransaction? tx)
    {
        var votes = new List<(string User, string Activity, long Approved)>();
        using var read = HubStore.Command(db, "SELECT user,activity,approved FROM deletion_votes WHERE proposal=$0 ORDER BY user LIMIT 10001", row.Id); read.Transaction = tx;
        using (var r = read.ExecuteReader()) while (r.Read()) votes.Add((r.GetString(0), r.GetString(1), r.GetInt64(2)));
        if (votes.Count > 10000) throw new HubError("Proposal consent exceeds its supported size.");
        return JsonSerializer.Serialize(new { Proposal = row, Votes = votes.Select(v => new { v.User, v.Activity, v.Approved }) });
    }
    private void CheckAuthorization(SqliteConnection db, QueuedDeletion row, SqliteTransaction? tx = null)
    {
        if (Get(db, row.Id, tx) != row) throw new HubError("Proposal changed. Refresh before acting.");
        using var read = HubStore.Command(db, "SELECT integrity FROM deletion_authorizations WHERE proposal=$0", row.Id); read.Transaction = tx;
        if (read.ExecuteScalar() is not string signature || !settings.Authenticated("MediaHub.DeletionConsent.v1", AuthorizationData(db, row, tx), signature))
            throw new HubError("Proposal consent or deadline integrity could not be verified. Undo and explicitly propose again; no removal was authorized.");
    }
    internal void SealQueue(SqliteConnection db, string id, SqliteTransaction? tx = null)
    {
        var row = Get(db, id, tx);
        var signature = settings.Authenticate("MediaHub.DeletionConsent.v1", AuthorizationData(db, row, tx));
        using var write = HubStore.Command(db, "INSERT INTO deletion_authorizations VALUES($0,$1) ON CONFLICT(proposal) DO UPDATE SET integrity=excluded.integrity", id, signature); write.Transaction = tx; write.ExecuteNonQuery();
    }
    private bool HasAuthorization(SqliteConnection db, QueuedDeletion row, SqliteTransaction tx)
    {
        try { CheckAuthorization(db, row, tx); return true; }
        catch (HubError) { return false; } // May block/cancel untrusted old state, but must not manufacture a valid seal.
    }
}
