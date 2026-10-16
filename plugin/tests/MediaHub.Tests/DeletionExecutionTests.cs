using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class DeletionExecutionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-exec-" + Guid.NewGuid());
    private readonly User user = new("Owner", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly Guid item = Guid.NewGuid();
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly Mock<IViewerMedia> media = new();
    private readonly Mock<IRecentActivity> activity = new();
    private readonly Mock<IRequestAuthority> authority = new();
    private readonly Mock<IArrDeletion> backend = new();
    private readonly ArrProof proof;
    private IReadOnlyList<RecentViewer> viewers = [];
    public DeletionExecutionTests()
    {
        store = new(directory); settings = new(store); settings.Update(new(0, false, "", [], "", null, null, AutomaticDeletion: true));
        media.Setup(m => m.Find(user, item)).Returns(new VisibleItem(item, "Movie", "Movie"));
        activity.Setup(a => a.Read(item)).Returns(() => viewers);
        authority.Setup(a => a.Verify(user, 1, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "pin"));
        proof = new(new("https://seerr", 1, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv"), "https://arr", "hash", 0, 7, "/movies/title", [new(9, "/movies/title/file.mkv", 100, "2026-10-16")]);
        backend.Setup(b => b.Capture(user, It.IsAny<VerifiedDeletionTarget>(), It.IsAny<CancellationToken>())).ReturnsAsync(proof);
        backend.Setup(b => b.Prepare(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PreparedArr(proof, "secret"));
        backend.Setup(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        backend.Setup(b => b.Gone(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        backend.Setup(b => b.Unmonitor(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }
    private DeletionQueue Queue(bool withBackend = true) => new(store, settings, new PersonalService(store, media.Object), media.Object, activity.Object, authority.Object, withBackend ? backend.Object : null);
    private async Task<string> Due(DeletionQueue queue)
    {
        await queue.Propose(user, 1, item, CancellationToken.None); var id = Assert.Single(queue.List(user)).Id;
        using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE deletion_queue SET state='ready',eligible=$0,due=$1,revision=revision+1 WHERE id=$2", DateTimeOffset.UtcNow.AddDays(-3).ToString("O"), DateTimeOffset.UtcNow.AddDays(-1).ToString("O"), id); update.ExecuteNonQuery(); queue.SealQueue(db, id); return id;
    }
    [Fact]
    public async Task ExecutesOneArrRequestThenCheckpointsAndUnmonitors()
    {
        var queue = Queue(); await Due(queue); await queue.Execute(user, CancellationToken.None);
        Assert.Empty(queue.List(user)); backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Once);
        backend.Verify(b => b.Unmonitor(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>()), Times.Once);
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT proof,state FROM deletion_runs"); using var r = read.ExecuteReader(); Assert.True(r.Read());
        Assert.DoesNotContain("/movies", r.GetString(0)); Assert.DoesNotContain("https://arr", r.GetString(0)); Assert.Equal("completed", r.GetString(1));
    }
    [Fact]
    public async Task AmbiguousDeleteIsNeverResentAfterRestartButCanReconcileGoneFiles()
    {
        var queue = Queue(); await Due(queue);
        backend.Setup(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TimeoutException());
        backend.Setup(b => b.Gone(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await queue.Execute(user, CancellationToken.None); Assert.Equal("uncertain", Assert.Single(queue.List(user)).State);
        await Queue().Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Once);
        backend.Setup(b => b.Gone(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Queue().Execute(user, CancellationToken.None); Assert.Empty(queue.List(user));
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task CleanupFailureRetriesOnlyMonitoringEvenAfterJellyfinItemDisappears()
    {
        var queue = Queue(); await Due(queue);
        backend.Setup(b => b.Unmonitor(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await queue.Execute(user, CancellationToken.None); Assert.Equal("cleanup", Assert.Single(queue.List(user)).State);
        authority.Setup(a => a.Verify(user, 1, item, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        media.Setup(m => m.Find(user, item)).Returns((VisibleItem?)null);
        await queue.Refresh(user, CancellationToken.None); queue.Block(user.Id);
        backend.Setup(b => b.Unmonitor(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await Queue().Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Once);
        backend.Verify(b => b.Gone(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>()), Times.Once);
        backend.Verify(b => b.Unmonitor(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Fact]
    public async Task NewViewerDuringPreparationBlocksDeletion()
    {
        var queue = Queue(); await Due(queue);
        backend.Setup(b => b.Prepare(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        { viewers = [new(Guid.NewGuid(), DateTimeOffset.UtcNow)]; return new(proof, "secret"); });
        await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("blocked", Assert.Single(queue.List(user)).State);
    }
    [Fact]
    public async Task UndoWinningClaimRacePreventsOutgoingDelete()
    {
        var queue = Queue(); var id = await Due(queue);
        backend.Setup(b => b.Prepare(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => { queue.Undo(user, id); return new(proof, "secret"); });
        await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(queue.List(user));
    }
    [Fact]
    public async Task DisabledAutomationAndLegacyUnpinnedProposalsNeverDelete()
    {
        var queue = Queue(); await Due(queue);
        settings.Update(new(1, false, "", [], "", null, null, AutomaticDeletion: false));
        await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        queue.Undo(user, Assert.Single(queue.List(user)).Id);
        var legacy = Queue(false); await Due(legacy);
        settings.Update(new(2, false, "", [], "", null, null, AutomaticDeletion: true));
        await Queue().Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("blocked", Assert.Single(legacy.List(user)).State);
    }
    [Fact]
    public async Task ForgedFilesDeletedStateCannotAuthorizeMonitoringCleanup()
    {
        var queue = Queue(); await Due(queue);
        using (var db = store.Open()) using (var update = HubStore.Command(db, "UPDATE deletion_runs SET state='files_deleted'")) update.ExecuteNonQuery();
        await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        backend.Verify(b => b.Unmonitor(It.IsAny<User>(), It.IsAny<ArrProof>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("uncertain", Assert.Single(queue.List(user)).State);
    }
    [Theory]
    [InlineData("deadline")] [InlineData("vote")] [InlineData("missing")]
    public async Task AlteredConsentOrDeadlinesCannotAuthorizeRemoval(string field)
    {
        var queue = Queue(); var id = await Due(queue);
        using (var db = store.Open()) using (var change = HubStore.Command(db, field switch {
            "deadline" => "UPDATE deletion_queue SET due='2000-01-01T00:00:00.0000000+00:00' WHERE id=$0",
            "vote" => "INSERT INTO deletion_votes VALUES($0,'forged-viewer','2026-10-16T00:00:00.0000000+00:00',1)",
            _ => "DELETE FROM deletion_authorizations WHERE proposal=$0" }, id)) change.ExecuteNonQuery();
        await queue.Refresh(user, CancellationToken.None); await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        backend.Verify(b => b.Unmonitor(It.IsAny<User>(), It.IsAny<ArrProof>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task VoteTamperingDuringPreparationCannotPassTheAtomicClaim()
    {
        var queue = Queue(); var id = await Due(queue);
        backend.Setup(b => b.Prepare(user, It.IsAny<ArrProof>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            using var db = store.Open(); using var change = HubStore.Command(db, "INSERT INTO deletion_votes VALUES($0,$1,$2,1)", id, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.ToString("O")); change.ExecuteNonQuery();
            return new(proof, "secret");
        });
        await queue.Execute(user, CancellationToken.None);
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task SchemaTenUpgradeRetainsButDoesNotBlessOldConsentAndAllowsOwnerUndo()
    {
        var queue = Queue(); var id = await Due(queue);
        using (var db = store.Open()) using (var downgrade = HubStore.Command(db, "DROP TABLE deletion_authorizations; PRAGMA user_version=10")) downgrade.ExecuteNonQuery();
        var upgraded = new HubStore(directory); var upgradedQueue = new DeletionQueue(upgraded, new SettingsService(upgraded), new PersonalService(upgraded, media.Object), media.Object, activity.Object, authority.Object, backend.Object);
        using (var db = upgraded.Open()) using (var read = HubStore.Command(db, "SELECT COUNT(*) FROM deletion_authorizations")) Assert.Equal(0L, read.ExecuteScalar());
        await upgradedQueue.Refresh(user, CancellationToken.None); await upgradedQueue.Execute(user, CancellationToken.None);
        Assert.Equal("blocked", Assert.Single(upgradedQueue.List(user)).State); upgradedQueue.Undo(user, id); Assert.Empty(upgradedQueue.List(user));
        backend.Verify(b => b.Delete(It.IsAny<PreparedArr>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
