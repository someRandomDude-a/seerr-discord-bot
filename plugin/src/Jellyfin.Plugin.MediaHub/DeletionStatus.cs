using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record PersonalDeletionStatus(string Proposal, Guid Item, string Title, string State, DateTimeOffset? DueAt,
    bool AutomaticDeletion, bool Yours, bool CanUndo, string RequiredAction, DateTimeOffset? YourLastActivity,
    bool ActivityVerified, string YourConsent, string? Notice, int ActivityWindowDays, int DelayDays);

public sealed partial class DeletionQueue
{
    public PersonalDeletionStatus Status(User viewer, string id)
    {
        var row = Get(id); var visible = media.Find(viewer, row.Item) ?? media.FindForConsent(viewer, row.Item) ?? throw new HubError("Proposal unavailable.");
        var config = settings.Current;
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true);
        CheckAuthorization(db, row, tx);
        DateTimeOffset? own = null; var verified = false;
        try { own = activity.Read(row.Item).Where(v => v.UserId == viewer.Id).MaxBy(v => v.LastActivity)?.LastActivity; verified = true; }
        catch { /* Unknown activity never becomes 'no viewers' or authorization. */ }
        var consent = "not_required";
        string? notice = null;
        if (!verified) consent = "unknown";
        else if (own >= DateTimeOffset.UtcNow.AddDays(-config.RecentActivityDays))
        {
            consent = "required";
            using var vote = HubStore.Command(db, "SELECT activity,approved FROM deletion_votes WHERE proposal=$0 AND user=$1", id, Key(viewer.Id)); vote.Transaction = tx;
            using (var r = vote.ExecuteReader()) if (r.Read() && DateTimeOffset.Parse(r.GetString(0)) == own) consent = r.GetInt64(1) == 1 ? "yes" : "no";
            using var read = HubStore.Command(db, "SELECT id FROM deletion_notices WHERE proposal=$0 AND user=$1 AND activity=$2 LIMIT 1", id, Key(viewer.Id), Time(own)); read.Transaction = tx;
            notice = read.ExecuteScalar() as string;
        }
        var terminal = row.State is "cancelled" or "completed";
        var action = terminal ? "none" : !verified || row.State is "blocked" or "uncertain" ? "review_required" : consent == "required" ? "consent_required" : consent == "no" ? "blocked_by_your_choice" : !config.AutomaticDeletion && row.State is "ready" ? "automatic_deletion_off" : "wait";
        var yours = row.Owner == viewer.Id;
        tx.Commit();
        return new(row.Id, visible.Id, visible.Title, row.State, row.Due, config.AutomaticDeletion, yours,
            yours && row.State is "waiting" or "queued" or "ready" or "blocked", action, own, verified, consent, notice, config.RecentActivityDays, row.Delay);
    }
}
