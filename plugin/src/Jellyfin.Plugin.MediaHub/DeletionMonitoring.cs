using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record AuditAccount(Guid Id, string Name, bool Exists, bool Disabled);
public sealed record MonitorProposal(string Id, Guid Item, string Title, AuditAccount Requester, int RequestId,
    string State, DateTimeOffset Created, DateTimeOffset? EligibleSince, DateTimeOffset? DueAt, int DelayDays,
    long Revision, string Integrity, string? Checkpoint);
public sealed record MonitorPage(IReadOnlyList<MonitorProposal> Items, int Page, int PageSize, long Total,
    IReadOnlyDictionary<string, long> Counts, IReadOnlyList<AuditAccount> Accounts, DateTimeOffset GeneratedAt,
    bool AutomaticDeletion, bool HistoricalTransitionsAvailable = false);
public sealed record AuditActivity(AuditAccount Account, DateTimeOffset LastActivity);
public sealed record AuditVote(AuditAccount Account, DateTimeOffset Activity, bool Approved);
public sealed record AuditNotice(string Id, AuditAccount Account, DateTimeOffset Activity, string? Delivery, DateTimeOffset? Updated);
public sealed record MonitorDetails(MonitorProposal Proposal, string Target, ArrProof? Proof, string ProofIntegrity,
    DateTimeOffset? CheckpointUpdated, IReadOnlyList<AuditActivity> Activity, bool ActivityVerified,
    IReadOnlyList<AuditVote> Votes, IReadOnlyList<AuditNotice> Notices, bool HistoricalTransitionsAvailable = false);

/// <summary>Native administrator-only, read-only snapshots. Never projects through ordinary queue DTOs.</summary>
public sealed class DeletionMonitoring(HostIdentity identity, IUserManager users, ILibraryManager library, DeletionQueue queue)
{
    private User Administrator(Guid id)
    {
        var user = identity.Active(id);
        if (!user.HasPermission(PermissionKind.IsAdministrator)) throw new HubError("Native administrator access is required.", "admin_denied");
        return user;
    }
    private AuditAccount Account(Guid id)
    {
        var user = users.GetUserById(id);
        return new(id, user?.Username ?? "Deleted or unavailable account", user is not null, user?.HasPermission(PermissionKind.IsDisabled) == true);
    }
    private string? Title(Guid item) => library.GetItemById(item)?.Name;
    public MonitorPage Browse(Guid administrator, string state = "backlog", string query = "", Guid? owner = null, int page = 1, string sort = "oldest")
    {
        var user = Administrator(administrator);
        var result = queue.Monitor(user, state, query, owner, page, sort, Account, Title);
        Administrator(administrator); return result;
    }
    public MonitorDetails Details(Guid administrator, string id)
    {
        var user = Administrator(administrator); var result = queue.MonitorDetail(user, id, Account, Title);
        Administrator(administrator); return result;
    }
    public DeletionHistoryPage History(Guid administrator, string id, int page = 1)
    {
        var result = queue.History(Administrator(administrator), id, page, Account); Administrator(administrator); return result;
    }
}

public sealed partial class DeletionQueue
{
    private static readonly string[] MonitorStates = ["waiting", "queued", "ready", "blocked", "executing", "checking", "cleanup", "uncertain", "cancelled", "completed"];
    private static void RequireMonitor(User user)
    { if (!user.HasPermission(PermissionKind.IsAdministrator) || user.HasPermission(PermissionKind.IsDisabled)) throw new HubError("Native administrator access is required.", "admin_denied"); }
    private MonitorProposal MonitorRow(SqliteConnection db, SqliteTransaction tx, QueuedDeletion row, Func<Guid, AuditAccount> account, Func<Guid, string?> title)
    {
        var integrity = HasAuthorization(db, row, tx) ? "verified" : "unverified";
        var displayTitle = title(row.Item);
        if (displayTitle is null && integrity == "verified" && settings.Protection.CheckAudit(db, tx, row.Id) is not null)
            displayTitle = ReadAudit(db, tx, row.Id, 1).Title;
        using var run = HubStore.Command(db, "SELECT state FROM deletion_runs WHERE proposal=$0", row.Id); run.Transaction = tx;
        return new(row.Id, row.Item, displayTitle ?? "Removed or unavailable item", account(row.Owner), row.Request, row.State, row.Created,
            row.Eligible, row.Due, row.Delay, row.Revision, integrity, run.ExecuteScalar() as string);
    }
    internal MonitorPage Monitor(User user, string state, string query, Guid? owner, int page, string sort, Func<Guid, AuditAccount> account, Func<Guid, string?> title)
    {
        RequireMonitor(user);
        if (!(state is "all" or "backlog" or "history") && !MonitorStates.Contains(state, StringComparer.Ordinal) ||
            query is null || query.Length > 100 || query.Any(char.IsControl) || page is < 1 or > 200000 || sort is not ("oldest" or "newest" or "due"))
            throw new HubError("Invalid monitoring filter.");
        const int size = 25;
        const string filter = """
            WHERE ($0='all' OR ($0='backlog' AND state NOT IN ('cancelled','completed')) OR
                ($0='history' AND state IN ('cancelled','completed')) OR state=$0)
            AND ($1 IS NULL OR owner=$1)
            AND ($2='' OR instr(lower(id),lower($2))>0 OR instr(CAST(request_id AS TEXT),$2)>0)
            """;
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true);
        var counts = new Dictionary<string, long>();
        using (var count = HubStore.Command(db, "SELECT state,COUNT(*) FROM deletion_queue GROUP BY state"))
        { count.Transaction = tx; using var r = count.ExecuteReader(); while (r.Read()) { if (counts.Count >= 50) throw new HubError("Monitoring state scan exceeds its supported budget."); counts.Add(r.GetString(0), r.GetInt64(1)); } }
        long total;
        using (var count = HubStore.Command(db, "SELECT COUNT(*) FROM deletion_queue " + filter, state, owner is null ? null : Key(owner.Value), query.Trim()))
        { count.Transaction = tx; total = (long)count.ExecuteScalar()!; }
        var order = sort switch { "newest" => "created DESC,id DESC", "due" => "due IS NULL,due,created,id", _ => "created,id" };
        var ids = new List<string>();
        using (var read = HubStore.Command(db, "SELECT id FROM deletion_queue " + filter + " ORDER BY " + order + " LIMIT $3 OFFSET $4", state, owner is null ? null : Key(owner.Value), query.Trim(), size, checked((page - 1) * size)))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) ids.Add(r.GetString(0)); }
        var rows = ids.Select(id => MonitorRow(db, tx, Get(db, id, tx), account, title)).ToArray();
        var accounts = new List<AuditAccount>();
        using (var read = HubStore.Command(db, "SELECT DISTINCT owner FROM deletion_queue ORDER BY owner LIMIT 501"))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) accounts.Add(account(Guid.Parse(r.GetString(0)))); }
        if (accounts.Count > 500) throw new HubError("Account filter exceeds its supported budget. No partial monitor snapshot was presented.");
        var automatic = settings.Current.AutomaticDeletion;
        var historyAvailable = rows.Any(row => row.Integrity == "verified" && settings.Protection.CheckAudit(db, tx, row.Id) is not null);
        RequireMonitor(user); tx.Commit();
        return new(rows, page, size, total, counts, accounts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray(), DateTimeOffset.UtcNow, automatic, historyAvailable);
    }
    internal MonitorDetails MonitorDetail(User user, string id, Func<Guid, AuditAccount> account, Func<Guid, string?> title)
    {
        RequireMonitor(user);
        if (id is null || id.Length != 48 || !id.All(Uri.IsHexDigit)) throw new HubError("Proposal unavailable.");
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true); var row = Get(db, id, tx);
        var projection = MonitorRow(db, tx, row, account, title); var historyAvailable = false;
        try { historyAvailable = settings.Protection.CheckAudit(db, tx, id) is not null; }
        catch (HubError) { /* Keep the current-state integrity warning; never manufacture history. */ }
        ArrProof? proof = null; var proofIntegrity = "not recorded"; DateTimeOffset? updated = null;
        using (var read = HubStore.Command(db, "SELECT proof,state,updated FROM deletion_runs WHERE proposal=$0", id))
        {
            read.Transaction = tx; using var r = read.ExecuteReader();
            if (r.Read())
            {
                updated = DateTimeOffset.Parse(r.GetString(2)); proofIntegrity = "unverified";
                try { proof = SafeJson.Read<ArrProof>(settings.UnprotectDeletionProof(r.GetString(0), ProofPurpose(id, row.Owner, row.Item, r.GetString(1))), SettingsService.DeletionProofLimit); proofIntegrity = "verified"; }
                catch { /* Show the verification failure, never ciphertext or a fabricated proof. */ }
            }
        }
        var votes = new List<AuditVote>();
        using (var read = HubStore.Command(db, "SELECT user,activity,approved FROM deletion_votes WHERE proposal=$0 ORDER BY user LIMIT 10001", id))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) votes.Add(new(account(Guid.Parse(r.GetString(0))), DateTimeOffset.Parse(r.GetString(1)), r.GetInt64(2) == 1)); }
        var notices = new List<AuditNotice>();
        using (var read = HubStore.Command(db, "SELECT n.id,n.user,n.activity,d.state,d.updated FROM deletion_notices n LEFT JOIN consent_delivery d ON d.notice=n.id WHERE n.proposal=$0 ORDER BY n.activity,n.id LIMIT 10001", id))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) notices.Add(new(r.GetString(0), account(Guid.Parse(r.GetString(1))), DateTimeOffset.Parse(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3), Parse(r, 4))); }
        if (votes.Count > 10000 || notices.Count > 10000) throw new HubError("Audit evidence exceeds its supported budget. No partial snapshot was presented.");
        RequireMonitor(user); tx.Commit();
        IReadOnlyList<AuditActivity> viewers = []; var activityVerified = false;
        try { viewers = activity.Read(row.Item).Select(v => new AuditActivity(account(v.UserId), v.LastActivity)).ToArray(); activityVerified = true; }
        catch { /* Incomplete native/local activity is unknown, never an empty verified scan. */ }
        RequireMonitor(user); return new(projection, row.Target, proof, proofIntegrity, updated, viewers, activityVerified, votes, notices, historyAvailable);
    }
}
