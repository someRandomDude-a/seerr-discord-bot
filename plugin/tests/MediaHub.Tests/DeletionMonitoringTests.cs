using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class DeletionMonitoringTests : IDisposable
{
    private readonly FeatureFixture f = new();
    private readonly Guid item = Guid.NewGuid();
    private readonly Mock<IViewerMedia> media = new();
    private readonly Mock<IRecentActivity> activity = new();
    private readonly Mock<IRequestAuthority> authority = new();
    private readonly DeletionQueue queue;
    private readonly DeletionMonitoring monitor;
    private readonly User admin = new("Audit administrator", "provider", "reset") { Id = Guid.NewGuid() };
    public DeletionMonitoringTests()
    {
        admin.SetPermission(PermissionKind.IsAdministrator, true); f.Users.Setup(u => u.GetUserById(admin.Id)).Returns(admin);
        media.Setup(m => m.Find(f.User, item)).Returns(new VisibleItem(item, "Visible movie", "Movie"));
        activity.Setup(a => a.Read(item)).Returns([new RecentViewer(f.User.Id, DateTimeOffset.UtcNow.AddHours(-1))]);
        authority.Setup(a => a.Verify(f.User, It.IsAny<int>(), item, It.IsAny<CancellationToken>())).ReturnsAsync((User _, int request, Guid _, CancellationToken _) => new VerifiedDeletionTarget(item, "pin-" + request));
        queue = new(f.Store, f.Settings, new(f.Store, media.Object, f.Settings), media.Object, activity.Object, authority.Object);
        monitor = new(f.Identity, f.Users.Object, f.Library.Object, queue);
    }
    [Fact]
    public async Task NonAdministratorCannotReadBacklogOrEvidenceEvenWithAValidLink()
    {
        await queue.Propose(f.User, 1, item, default); var id = Assert.Single(queue.List(f.User)).Id;
        Assert.Equal("admin_denied", Assert.Throws<HubError>(() => monitor.Browse(f.User.Id)).Code);
        Assert.Equal("admin_denied", Assert.Throws<HubError>(() => monitor.Details(f.User.Id, id)).Code);
    }
    [Fact]
    public async Task AdministratorCanBrowseCancelledHistoryWithoutRestoringVisibilityOrExposingItToOrdinaryQueue()
    {
        await queue.Propose(f.User, 1, item, default); var id = Assert.Single(queue.List(f.User)).Id; queue.Undo(f.User, id);
        Assert.Empty(queue.List(f.User)); Assert.Empty(monitor.Browse(admin.Id).Items);
        var history = monitor.Browse(admin.Id, "history"); var row = Assert.Single(history.Items);
        Assert.Equal(id, row.Id); Assert.Equal(f.User.Id, row.Requester.Id); Assert.Equal("Viewer", row.Requester.Name);
        Assert.Equal("cancelled", row.State); Assert.Equal("verified", row.Integrity); Assert.Equal("Visible movie", row.Title);
        Assert.True(history.HistoricalTransitionsAvailable); Assert.Equal(1, history.Counts["cancelled"]);
    }
    [Fact]
    public async Task EvidenceIncludesIdentityActivityAndVotesButNeverPretendsUnavailableActivityIsEmpty()
    {
        await queue.Propose(f.User, 1, item, default); var id = Assert.Single(queue.List(f.User)).Id;
        var detail = monitor.Details(admin.Id, id); Assert.True(detail.ActivityVerified);
        Assert.Equal(f.User.Id, Assert.Single(detail.Activity).Account.Id); Assert.True(Assert.Single(detail.Votes).Approved);
        Assert.NotEmpty(detail.Notices); Assert.Null(detail.Proof); Assert.Equal("not recorded", detail.ProofIntegrity);
        activity.Setup(a => a.Read(item)).Throws(new IOException("private activity failure"));
        detail = monitor.Details(admin.Id, id); Assert.False(detail.ActivityVerified); Assert.Empty(detail.Activity);
        Assert.DoesNotContain("private activity failure", JsonSerializer.Serialize(detail));
    }
    [Fact]
    public async Task CorruptConsentIsLabelledUnverifiedAndBrowsingDoesNotResealOrResetIt()
    {
        await queue.Propose(f.User, 1, item, default); var id = Assert.Single(queue.List(f.User)).Id;
        using (var db = f.Store.Open()) using (var tamper = HubStore.Command(db, "UPDATE deletion_queue SET delay=3 WHERE id=$0", id)) tamper.ExecuteNonQuery();
        Assert.Equal("unverified", Assert.Single(monitor.Browse(admin.Id).Items).Integrity);
        Assert.Equal("unverified", monitor.Details(admin.Id, id).Proposal.Integrity);
        using var check = f.Store.Open(); using var read = HubStore.Command(check, "SELECT delay FROM deletion_queue WHERE id=$0", id);
        Assert.Equal(3L, read.ExecuteScalar());
    }
    [Fact]
    public async Task PaginationFiltersAndOrderingAreBoundedAndDoNotHideTerminalHistory()
    {
        for (var i = 1; i <= 28; i++) await queue.Propose(f.User, i, item, default);
        var first = monitor.Browse(admin.Id, "all", sort: "newest"); Assert.Equal(25, first.Items.Count); Assert.Equal(28, first.Total);
        var second = monitor.Browse(admin.Id, "all", page: 2, sort: "newest"); Assert.Equal(3, second.Items.Count);
        Assert.Empty(first.Items.Select(row => row.Id).Intersect(second.Items.Select(row => row.Id)));
        Assert.Equal(1, Assert.Single(monitor.Browse(admin.Id, "all", query: "1", owner: f.User.Id).Items, r => r.RequestId == 1).RequestId);
        Assert.Empty(monitor.Browse(admin.Id, "all", owner: Guid.NewGuid()).Items);
        Assert.Throws<HubError>(() => monitor.Browse(admin.Id, "invented")); Assert.Throws<HubError>(() => monitor.Browse(admin.Id, page: 0));
        Assert.Throws<HubError>(() => monitor.Browse(admin.Id, sort: "created; DROP TABLE deletion_queue"));
    }
    [Fact]
    public async Task LostAdministrationOrDisabledAccountIsRejectedImmediately()
    {
        await queue.Propose(f.User, 1, item, default); Assert.NotEmpty(monitor.Browse(admin.Id).Items);
        admin.SetPermission(PermissionKind.IsAdministrator, false); Assert.Throws<HubError>(() => monitor.Browse(admin.Id));
        admin.SetPermission(PermissionKind.IsAdministrator, true); admin.SetPermission(PermissionKind.IsDisabled, true);
        Assert.Throws<HubError>(() => monitor.Browse(admin.Id));
    }
    [Fact]
    public async Task NativeEndpointRejectsApiKeysAndCannotAcceptCallerSelectedAdministrator()
    {
        using var services = new ServiceCollection().AddSingleton(f.Identity).AddSingleton(monitor).BuildServiceProvider();
        var controller = new DeletionMonitoringController(services) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-IsApiKey", "True"), new("Jellyfin-UserId", admin.Id.ToString())], "ApiKey")) } } };
        Assert.IsType<BadRequestObjectResult>(await controller.Browse());
        Assert.All(typeof(DeletionMonitoringController).GetMethods().Where(m => m.DeclaringType == typeof(DeletionMonitoringController)), method => Assert.DoesNotContain(method.GetParameters(), p => p.Name is "administrator" or "actor"));
    }
    public void Dispose() => f.Dispose();
}
