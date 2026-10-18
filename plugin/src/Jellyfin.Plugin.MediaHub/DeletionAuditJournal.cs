using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

internal sealed record StoredDeletionEvent(DateTimeOffset At, string Kind, string State, DateTimeOffset? DueAt,
    Guid? Actor, string? Reason, string? Title, bool CompleteFromCreation);
public sealed record DeletionHistoryEvent(long Sequence, DateTimeOffset At, string Kind, string State,
    DateTimeOffset? DueAt, AuditAccount? Actor, string? Reason);
public sealed record DeletionHistoryPage(IReadOnlyList<DeletionHistoryEvent> Items, int Page, int PageSize,
    long Total, bool CompleteFromCreation, string Integrity);

public sealed partial class DeletionQueue
{
    private static string AuditPurpose(string id, long sequence) => "MediaHub.DeletionAudit.v1|" + id + "|" + sequence;
    private void AppendAudit(SqliteConnection db, SqliteTransaction tx, QueuedDeletion row, string kind, Guid? actor, string? reason, string? title)
    {
        settings.Protection.CheckAudit(db, tx, row.Id);
        using var count = HubStore.Command(db, "SELECT COUNT(*) FROM deletion_audit WHERE proposal=$0", row.Id); count.Transaction = tx;
        var total = (long)count.ExecuteScalar()!;
        if (total >= 10000) throw new HubError("Audit history capacity reached. Nothing was discarded.");
        var complete = total == 0 ? kind == "proposed" : ReadAudit(db, tx, row.Id, 1).CompleteFromCreation;
        var value = new StoredDeletionEvent(DateTimeOffset.UtcNow, kind, row.State, row.Due, actor, reason,
            title is null ? null : new string(title.Where(c => !char.IsControl(c)).Take(300).ToArray()), complete);
        using var add = HubStore.Command(db, "INSERT INTO deletion_audit VALUES($0,$1,$2)", row.Id, total + 1,
            settings.Protect(JsonSerializer.Serialize(value), AuditPurpose(row.Id, total + 1))); add.Transaction = tx; add.ExecuteNonQuery();
    }
    private StoredDeletionEvent ReadAudit(SqliteConnection db, SqliteTransaction? tx, string id, long sequence)
    {
        using var read = HubStore.Command(db, "SELECT payload FROM deletion_audit WHERE proposal=$0 AND sequence=$1", id, sequence); read.Transaction = tx;
        if (read.ExecuteScalar() is not string payload) throw new HubError("Deletion audit event is missing.");
        return SafeJson.Read<StoredDeletionEvent>(settings.Unprotect(payload, AuditPurpose(id, sequence)), 8192);
    }
    internal DeletionHistoryPage History(Jellyfin.Database.Implementations.Entities.User user, string id, int page, Func<Guid, AuditAccount> account)
    {
        RequireMonitor(user); if (page is < 1 or > 400 || id.Length != 48 || !id.All(Uri.IsHexDigit)) throw new HubError("Invalid audit history page.");
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true); var row = Get(db, id, tx);
        var head = settings.Protection.CheckAudit(db, tx, id);
        using var count = HubStore.Command(db, "SELECT COUNT(*) FROM deletion_audit WHERE proposal=$0", id); count.Transaction = tx; var total = (long)count.ExecuteScalar()!;
        var result = new List<DeletionHistoryEvent>(); const int size = 25;
        for (var sequence = Math.Max(1, total - page * size + 1); sequence <= total - (page - 1) * size; sequence++)
        {
            var value = ReadAudit(db, tx, id, sequence);
            result.Add(new(sequence, value.At, value.Kind, value.State, value.DueAt, value.Actor is Guid actor ? account(actor) : null, value.Reason));
        }
        var complete = total > 0 && ReadAudit(db, tx, id, 1).CompleteFromCreation; tx.Commit(); RequireMonitor(user);
        return new(result.OrderByDescending(e => e.Sequence).ToArray(), page, size, total, complete, head is null ? "not recorded" : "verified");
    }
}
