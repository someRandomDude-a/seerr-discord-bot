namespace Jellyfin.Plugin.MediaHub;

public sealed record DeletionRules(int RecentActivityDays = 14, int DefaultDelayDays = 2)
{
    public void Validate()
    {
        if (RecentActivityDays is < 1 or > 3650 || DefaultDelayDays < 1 || DefaultDelayDays > RecentActivityDays)
            throw new HubError("Deletion delay must be between 1 day and the recent-activity window (maximum 3650 days).");
    }
    public int Delay(int? personalDays)
    {
        Validate();
        if (personalDays is not null && (personalDays < DefaultDelayDays || personalDays > RecentActivityDays))
            throw new HubError($"Choose a deletion delay from {DefaultDelayDays} to {RecentActivityDays} days.");
        return personalDays ?? DefaultDelayDays;
    }
}

// Trusted application inputs, not request bodies or viewer-facing projections.
public sealed record RecentViewer(Guid UserId, DateTimeOffset LastActivity);
public sealed record DeletionConsent(Guid UserId, DateTimeOffset ActivityApproved);
public enum DeletionStage { Unavailable, NotOwner, AwaitingConsent, Queued, Ready }
public sealed record DeletionDecision(DeletionStage Stage, DateTimeOffset? EligibleSince = null, DateTimeOffset? DueAt = null);

/// <summary>Pure eligibility rules only. Does not query users, send notifications or delete media.</summary>
public static class DeletionPolicy
{
    public static DeletionDecision Evaluate(DeletionRules rules, DateTimeOffset now, Guid actor, Guid originalRequester,
        bool ownershipVerified, bool activityComplete, IReadOnlyList<RecentViewer> activity,
        IReadOnlyList<DeletionConsent> consent, int? personalDelayDays = null,
        DateTimeOffset? eligibleSince = null, DateTimeOffset? previousDueAt = null)
    {
        var delay = rules.Delay(personalDelayDays);
        if (!ownershipVerified || !activityComplete) return new(DeletionStage.Unavailable);
        if (actor == Guid.Empty || originalRequester == Guid.Empty || actor != originalRequester) return new(DeletionStage.NotOwner);
        if (activity.Any(row => row.UserId == Guid.Empty || row.LastActivity > now)) return new(DeletionStage.Unavailable);
        var cutoff = now.AddDays(-rules.RecentActivityDays);
        var recent = activity.GroupBy(row => row.UserId).Select(group => group.MaxBy(row => row.LastActivity)!)
            .Where(row => row.LastActivity >= cutoff).ToArray();
        // A new interaction invalidates consent to an older activity snapshot.
        // Missing/declined consent remains blocking only while that viewer is recent.
        if (recent.Any(viewer => !consent.Any(vote => vote.UserId == viewer.UserId && vote.ActivityApproved == viewer.LastActivity)))
            return new(DeletionStage.AwaitingConsent);
        if (eligibleSince > now || (eligibleSince is null && previousDueAt is not null)) return new(DeletionStage.Unavailable);
        if (eligibleSince is not null && recent.Any(row => row.LastActivity > eligibleSince))
        { eligibleSince = null; previousDueAt = null; } // Reconsent after new activity starts a fresh full queue.
        var since = eligibleSince ?? now;
        var due = since.AddDays(delay);
        if (previousDueAt > due) due = previousDueAt.Value; // Preference/config changes never shorten an existing queue.
        return new(now >= due ? DeletionStage.Ready : DeletionStage.Queued, since, due);
    }
}
