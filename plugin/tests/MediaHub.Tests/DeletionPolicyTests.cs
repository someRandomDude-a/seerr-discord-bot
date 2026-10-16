using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class DeletionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid viewer = Guid.NewGuid();
    private DeletionDecision Evaluate(IReadOnlyList<RecentViewer>? activity = null, IReadOnlyList<DeletionConsent>? consent = null,
        DateTimeOffset? now = null, DateTimeOffset? since = null, DateTimeOffset? due = null, int? delay = null,
        bool complete = true, bool verified = true, Guid? actor = null) =>
        DeletionPolicy.Evaluate(new(), now ?? Now, actor ?? owner, owner, verified, complete,
            activity ?? [], consent ?? [], delay, since, due);
    [Fact]
    public void NoRecentViewersStartFullTwoDayQueue()
    {
        var result = Evaluate(); Assert.Equal(DeletionStage.Queued, result.Stage);
        Assert.Equal(Now.AddDays(2), result.DueAt);
        Assert.Equal(DeletionStage.Ready, Evaluate(now: Now.AddDays(2), since: result.EligibleSince, due: result.DueAt).Stage);
    }
    [Fact]
    public void OnlyVerifiedOriginalRequesterMaySchedule()
    {
        Assert.Equal(DeletionStage.NotOwner, Evaluate(actor: viewer).Stage);
        Assert.Equal(DeletionStage.Unavailable, Evaluate(verified: false).Stage);
        Assert.Equal(DeletionStage.Unavailable, Evaluate(complete: false).Stage);
    }
    [Fact]
    public void EveryRecentViewerMustExplicitlyAgree()
    {
        RecentViewer[] activity = [new(owner, Now.AddDays(-1)), new(viewer, Now.AddDays(-3))];
        DeletionConsent[] votes = [new(owner, activity[0].LastActivity)];
        Assert.Equal(DeletionStage.AwaitingConsent, Evaluate(activity, votes).Stage);
        var agreed = Evaluate(activity, [.. votes, new(viewer, activity[1].LastActivity)]);
        Assert.Equal(DeletionStage.Queued, agreed.Stage); Assert.Equal(Now.AddDays(2), agreed.DueAt);
    }
    [Fact]
    public void UnansweredOrDeclinedViewerStopsBlockingOnlyAfterAgingOut()
    {
        RecentViewer[] activity = [new(viewer, Now.AddDays(-14))];
        Assert.Equal(DeletionStage.AwaitingConsent, Evaluate(activity).Stage);
        var later = Now.AddSeconds(1); var result = Evaluate(activity, now: later);
        Assert.Equal(DeletionStage.Queued, result.Stage); Assert.Equal(later.AddDays(2), result.DueAt);
    }
    [Fact]
    public void NewActivityInvalidatesOldConsentAndClearsQueueClock()
    {
        var result = Evaluate([new(viewer, Now)], [new(viewer, Now.AddDays(-1))], since: Now.AddDays(-3), due: Now.AddDays(-1));
        Assert.Equal(DeletionStage.AwaitingConsent, result.Stage); Assert.Null(result.EligibleSince); Assert.Null(result.DueAt);
    }
    [Fact]
    public void MultipleEpisodesUseViewersLatestActivity()
    {
        RecentViewer[] activity = [new(viewer, Now.AddDays(-2)), new(viewer, Now.AddDays(-1))];
        Assert.Equal(DeletionStage.AwaitingConsent, Evaluate(activity, [new(viewer, Now.AddDays(-2))]).Stage);
        Assert.Equal(DeletionStage.Queued, Evaluate(activity, [new(viewer, Now.AddDays(-1))]).Stage);
    }
    [Fact]
    public void PersonalDelayCanIncreaseToWindowButNotShortenQueue()
    {
        Assert.Equal(Now.AddDays(14), Evaluate(delay: 14).DueAt);
        Assert.Throws<HubError>(() => Evaluate(delay: 1)); Assert.Throws<HubError>(() => Evaluate(delay: 15));
        Assert.Equal(Now.AddDays(14), Evaluate(since: Now, due: Now.AddDays(14)).DueAt);
    }
    [Fact]
    public void NewActivityWithFreshConsentStillRestartsFullDelay()
    {
        var result = Evaluate([new(viewer, Now.AddDays(-1))], [new(viewer, Now.AddDays(-1))], since: Now.AddDays(-3), due: Now.AddDays(-1));
        Assert.Equal(DeletionStage.Queued, result.Stage); Assert.Equal(Now.AddDays(2), result.DueAt);
    }
    [Fact]
    public void AdminAndPersonalSettingsValidateAndPersistTimingLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mediahub-policy-" + Guid.NewGuid());
        try
        {
            var store = new HubStore(directory); var settings = new SettingsService(store);
            settings.Update(new(0, false, "", [], "", null, null, 21, 3));
            Assert.Equal(21, new SettingsService(store).Current.RecentActivityDays);
            Assert.Throws<HubError>(() => settings.Update(new(1, false, "", [], "", null, null, 2, 3)));
            var user = new Jellyfin.Database.Implementations.Entities.User("User", "provider", "reset") { Id = owner };
            var personal = new PersonalService(store, new Moq.Mock<IViewerMedia>().Object, settings);
            Assert.Throws<HubError>(() => personal.Save(user, 0, new(DeletionDelayDays: 2)));
            Assert.Throws<HubError>(() => personal.Save(user, 0, new(DeletionDelayDays: 22)));
            Assert.Equal(10, personal.Save(user, 0, new(DeletionDelayDays: 10)).EffectiveDeletionDelayDays);
            Assert.Equal(10, new PersonalService(new HubStore(directory), new Moq.Mock<IViewerMedia>().Object, new SettingsService(store)).State(user).Preferences.DeletionDelayDays);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(0, 2)] [InlineData(14, 0)] [InlineData(1, 2)] [InlineData(3651, 2)]
    public void InvalidAdminLimitsAreRejected(int recent, int delay) => Assert.Throws<HubError>(() => new DeletionRules(recent, delay).Validate());
    [Fact]
    public void FutureOrIncompleteActivityCannotBecomePermissionToDelete()
    {
        Assert.Equal(DeletionStage.Unavailable, Evaluate([new(viewer, Now.AddMinutes(1))]).Stage);
        Assert.Equal(DeletionStage.Unavailable, Evaluate([new(Guid.Empty, Now)]).Stage);
        Assert.Equal(DeletionStage.Unavailable, Evaluate(since: Now.AddDays(1)).Stage);
    }
}
