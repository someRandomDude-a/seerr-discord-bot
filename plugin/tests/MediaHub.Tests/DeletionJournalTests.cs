using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class DeletionJournalTests : IDisposable
{
    private readonly FeatureFixture f = new();
    private readonly Guid item = Guid.NewGuid();
    private readonly Mock<IViewerMedia> media = new();
    private readonly Mock<IRecentActivity> activity = new();
    private readonly Mock<IRequestAuthority> authority = new();
    private readonly DeletionQueue queue;
    public DeletionJournalTests()
    {
        f.User.SetPermission(PermissionKind.IsAdministrator, true);
        media.Setup(m => m.Find(f.User, item)).Returns(new VisibleItem(item, "Private retained movie title", "Movie"));
        activity.Setup(a => a.Read(item)).Returns([]);
        authority.Setup(a => a.Verify(f.User, 1, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "pin"));
        queue = new(f.Store, f.Settings, new(f.Store, media.Object, f.Settings), media.Object, activity.Object, authority.Object);
    }
    private async Task<string> Proposal() { await queue.Propose(f.User, 1, item, default); return Assert.Single(queue.List(f.User)).Id; }
    private AuditAccount Account(Guid id) => new(id, "Native account", true, false);
    [Fact]
    public async Task CommittedCreationRefreshAndCancellationAreRecordedWithoutPollingNoiseOrPlaintextTitles()
    {
        var id = await Proposal(); var before = queue.History(f.User, id, 1, Account);
        Assert.True(before.CompleteFromCreation); Assert.Equal("verified", before.Integrity);
        Assert.Contains(before.Items, row => row.Kind == "proposed" && row.Actor?.Id == f.User.Id);
        await queue.Refresh(f.User, default); await queue.Refresh(f.User, default);
        Assert.Equal(before.Total, queue.History(f.User, id, 1, Account).Total);
        queue.Undo(f.User, id); var after = queue.History(f.User, id, 1, Account);
        Assert.Equal(before.Total + 1, after.Total); Assert.Equal("cancelled", after.Items[0].Kind);
        using var db = f.Store.Open(); using var read = HubStore.Command(db, "SELECT payload FROM deletion_audit WHERE proposal=$0", id); using var r = read.ExecuteReader();
        while (r.Read()) { Assert.DoesNotContain("Private retained movie title", r.GetString(0)); Assert.DoesNotContain(f.User.Id.ToString(), r.GetString(0)); }
        f.Data.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("delete_all")] [InlineData("delete_first")] [InlineData("tamper")]
    public async Task MissingOrAlteredJournalBlocksAuthorityWithoutResettingThePrivateRoot(string attack)
    {
        var id = await Proposal(); var root = Path.Combine(f.Store.DirectoryPath, "state-auth.json"); var original = File.ReadAllBytes(root);
        using (var db = f.Store.Open()) using (var write = HubStore.Command(db, attack switch {
            "delete_all" => "DELETE FROM deletion_audit WHERE proposal=$0",
            "delete_first" => "DELETE FROM deletion_audit WHERE proposal=$0 AND sequence=1",
            _ => "UPDATE deletion_audit SET payload='unverified' WHERE proposal=$0 AND sequence=1" }, id)) write.ExecuteNonQuery();
        Assert.Throws<HubError>(() => queue.Status(f.User, id)); Assert.Throws<HubError>(() => queue.History(f.User, id, 1, Account));
        Assert.Equal(original, File.ReadAllBytes(root)); Assert.NotNull(new SettingsService(f.Store).Current);
        // Audit failure does not rewrite or invalidate the independent personal-state seal.
        Assert.NotNull(new PersonalService(f.Store, media.Object, f.Settings).State(f.User));
    }
    [Fact]
    public async Task OldValidJournalAndQueueSealReplayIsRejectedByThePrivateHead()
    {
        var id = await Proposal(); string authorization; var oldEvents = new List<(long Sequence, string Payload)>();
        using (var db = f.Store.Open())
        {
            using var signature = HubStore.Command(db, "SELECT integrity FROM deletion_authorizations WHERE proposal=$0", id); authorization = (string)signature.ExecuteScalar()!;
            using var read = HubStore.Command(db, "SELECT sequence,payload FROM deletion_audit WHERE proposal=$0", id); using var r = read.ExecuteReader(); while (r.Read()) oldEvents.Add((r.GetInt64(0), r.GetString(1)));
        }
        queue.Undo(f.User, id);
        using (var db = f.Store.Open()) using (var tx = db.BeginTransaction())
        {
            using (var clear = HubStore.Command(db, "DELETE FROM deletion_audit WHERE proposal=$0", id)) { clear.Transaction = tx; clear.ExecuteNonQuery(); }
            foreach (var row in oldEvents) { using var write = HubStore.Command(db, "INSERT INTO deletion_audit VALUES($0,$1,$2)", id, row.Sequence, row.Payload); write.Transaction = tx; write.ExecuteNonQuery(); }
            using var seal = HubStore.Command(db, "UPDATE deletion_authorizations SET integrity=$0 WHERE proposal=$1", authorization, id); seal.Transaction = tx; seal.ExecuteNonQuery(); tx.Commit();
        }
        Assert.Throws<HubError>(() => queue.History(f.User, id, 1, Account)); Assert.Throws<HubError>(() => queue.Status(f.User, id));
    }
    [Fact]
    public async Task InterruptedPublicationAndDatabaseCommitFailsClosedWithoutPromotingAPreviousCheckpoint()
    {
        var id = await Proposal();
        using (var db = f.Store.Open()) using (var tx = db.BeginTransaction()) { queue.SealQueue(db, id, tx, "snapshot"); tx.Rollback(); }
        var root = File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "state-auth.json"));
        Assert.Throws<HubError>(() => queue.Status(f.User, id));
        var reopened = new SettingsService(new HubStore(f.Store.DirectoryPath)); Assert.NotNull(reopened.Current);
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "state-auth.json")));
        Assert.NotNull(new PersonalService(f.Store, media.Object, reopened).State(f.User));
    }
    [Fact]
    public void Schema13UpgradeIsAdditiveAndDoesNotRewriteThePrivateRootOrClaimOldHistory()
    {
        var root = File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "state-auth.json"));
        using (var db = f.Store.Open()) using (var schema = HubStore.Command(db, "DROP TABLE deletion_audit; PRAGMA user_version=13")) schema.ExecuteNonQuery();
        var upgraded = new HubStore(f.Store.DirectoryPath); var settings = new SettingsService(upgraded);
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(f.Store.DirectoryPath, "state-auth.json")));
        using var check = upgraded.Open(); using var count = HubStore.Command(check, "SELECT COUNT(*) FROM deletion_audit"); Assert.Equal(0L, count.ExecuteScalar());
        Assert.NotNull(settings.Current); Assert.NotEmpty(Directory.GetFiles(Path.Combine(f.Store.DirectoryPath, "backups"), "*.db"));
    }
    [Fact]
    public async Task ValidLegacyProposalRemainsUsableAndNewHistoryDoesNotInventEarlierEvents()
    {
        var id = HubStore.Nonce(); var now = DateTimeOffset.UtcNow;
        var row = new QueuedDeletion(id, f.User.Id, item, 1, "pin", "waiting", null, null, 0, now, 2);
        var authorization = f.Settings.Authenticate("MediaHub.DeletionConsent.v1", JsonSerializer.Serialize(new { Proposal = row, Votes = Array.Empty<object>() }));
        using (var db = f.Store.Open()) using (var tx = db.BeginTransaction())
        {
            using var add = HubStore.Command(db, "INSERT INTO deletion_queue VALUES($0,$1,$2,1,'pin','waiting',NULL,NULL,0,$3,2)", id, f.User.Id.ToString("N"), item.ToString("N"), now.ToString("O")); add.Transaction = tx; add.ExecuteNonQuery();
            using var seal = HubStore.Command(db, "INSERT INTO deletion_authorizations VALUES($0,$1)", id, authorization); seal.Transaction = tx; seal.ExecuteNonQuery(); tx.Commit();
        }
        Assert.Equal("waiting", queue.Status(f.User, id).State);
        var before = queue.History(f.User, id, 1, Account); Assert.Equal(0, before.Total); Assert.False(before.CompleteFromCreation);
        await queue.Refresh(f.User, default);
        var after = queue.History(f.User, id, 1, Account); Assert.Equal("verified", after.Integrity); Assert.False(after.CompleteFromCreation);
        Assert.DoesNotContain(after.Items, e => e.Kind == "proposed"); Assert.Equal("queued", queue.Status(f.User, id).State);
    }
    [Fact]
    public void LowDiskAndOverflowBudgetsDenyWritesRatherThanConsumingTheHostReserve()
    {
        Assert.Throws<HubError>(() => StateFiles.CheckSpaceBudget(StateFiles.FreeSpaceReserve - 1, 0));
        Assert.Throws<HubError>(() => StateFiles.CheckSpaceBudget(StateFiles.FreeSpaceReserve + 1024, 2048));
        Assert.Throws<HubError>(() => StateFiles.CheckSpaceBudget(long.MaxValue, -1));
        Assert.Throws<HubError>(() => StateFiles.CheckSpaceBudget(long.MaxValue, long.MaxValue));
        StateFiles.CheckSpaceBudget(StateFiles.FreeSpaceReserve + 4096, 2048);
    }
    public void Dispose() => f.Dispose();
}
