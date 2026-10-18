using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class SeerrFeatureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-seerr-" + Guid.NewGuid());
    private readonly User viewer = new("Viewer", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly HostIdentity identity;
    private readonly Mock<IUserManager> users = new();
    private readonly Mock<ILibraryManager> library = new();
    private readonly Mock<IUserDataManager> data = new();
    private readonly Server server;
    private readonly MediaCatalog catalog;
    private readonly SeerrBridge bridge;
    private readonly MediaRequests requests;
    public SeerrFeatureTests()
    {
        store = new(directory); settings = new(store); settings.Update(new(0, false, "", [], "https://seerr/base", null, "key"));
        users.Setup(u => u.GetUserById(viewer.Id)).Returns(viewer);
        identity = new(store, settings, users.Object, Mock.Of<ISessionManager>()); server = new(viewer.Id);
        bridge = new(store, settings, identity, () => new Handler(server));
        var activity = new MediaActivity(store, library.Object, users.Object, data.Object);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        catalog = new(bridge, library.Object, activity); requests = new(store, bridge, catalog, activity, settings);
    }
    [Fact]
    public async Task ReadsAndWritesAreScopedAndPermissionsNeverComeFromAdminContext()
    {
        using var scope = await bridge.Open(viewer, CancellationToken.None);
        Assert.Equal(7, scope.Viewer.Id); Assert.True(scope.Can(32)); Assert.False(scope.Can(2));
        var plan = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        var result = await requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("completed", result.State); Assert.Equal(22, result.RequestId);
        Assert.Equal(1, server.Writes);
        await requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None); Assert.Equal(1, server.Writes);
        Assert.All(server.Calls.Where(c => c.Path != "user?take=100&skip=0"), call => Assert.Equal("7", call.User));
        Assert.DoesNotContain(server.Bodies, body => body.Contains("userId") || body.Contains("serverId") || body.Contains("profileId"));
        data.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("impersonation")] [InlineData("incomplete")] [InlineData("disabled")]
    public async Task InvalidIdentityFailsClosedWithoutExternalWrites(string change)
    {
        server.Duplicate = change == "duplicate"; server.IgnoreUser = change == "impersonation"; server.Incomplete = change == "incomplete";
        if (change == "disabled") users.Setup(u => u.GetUserById(viewer.Id)).Returns((User?)null);
        await Assert.ThrowsAsync<HubError>(() => bridge.Open(viewer, CancellationToken.None)); Assert.Equal(0, server.Writes); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task MappingChangeAndPermissionsRevokedBeforeConfirmationPreventWrite()
    {
        var plan = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        server.Permissions = 0;
        await Assert.ThrowsAsync<HubError>(() => requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None));
        server.Permissions = 32; server.UserId = 8;
        await Assert.ThrowsAsync<HubError>(() => requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, server.Writes);
    }
    [Fact]
    public async Task OnlyOwnUnsentRequestPlanCanBeCancelled()
    {
        var plan = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        var other = new User("Other", "provider", "reset") { Id = Guid.NewGuid() };
        Assert.Throws<HubError>(() => requests.Cancel(other, plan.Id));
        Assert.Equal("cancelled", requests.Cancel(viewer, plan.Id).State);
        await requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None); Assert.Equal(0, server.Writes);
        var next = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        await requests.Confirm(viewer, next.Id, () => Task.CompletedTask, CancellationToken.None);
        Assert.Throws<HubError>(() => requests.Cancel(viewer, next.Id)); Assert.Equal(1, server.Writes);
    }
    [Fact]
    public async Task UncertainWriteIsNeverReplayedByNewServiceInstance()
    {
        var plan = await requests.Prepare(viewer, "tv", 123, false, [1], CancellationToken.None); server.LoseResponse = true;
        var result = await requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None); Assert.Equal("uncertain", result.State);
        var activity = new MediaActivity(store, library.Object, users.Object, data.Object);
        await new MediaRequests(store, bridge, catalog, activity, settings).Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(1, server.Writes);
        await Assert.ThrowsAsync<HubError>(() => requests.Prepare(viewer, "tv", 123, false, [1], CancellationToken.None));
        Assert.Throws<HubError>(() => requests.Receipt(new User("Other", "provider", "reset") { Id = Guid.NewGuid() }, plan.Id));
    }
    [Fact]
    public async Task BrowsingAndReadOnlyDetailsNeverWriteNativeActivityButExplicitDetailsDo()
    {
        var movie = new Mock<Movie>(); movie.Object.Id = Guid.NewGuid(); movie.Object.ProviderIds["Tmdb"] = "123";
        movie.Setup(m => m.IsVisibleStandalone(viewer)).Returns(true);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie.Object]);
        library.Setup(l => l.GetItemById(movie.Object.Id)).Returns(movie.Object);
        await catalog.Browse(viewer, "movie", "<script>", 1, CancellationToken.None);
        await catalog.Details(viewer, "movie", 123, CancellationToken.None); data.VerifyNoOtherCalls();
        using (var db = store.Open()) using (var count = HubStore.Command(db, "SELECT COUNT(*) FROM activity")) Assert.Equal(0L, count.ExecuteScalar());
        await catalog.Details(viewer, "movie", 123, CancellationToken.None, true);
        data.Verify(d => d.SaveUserData(viewer, movie.Object, It.IsAny<MediaBrowser.Model.Dto.UpdateUserItemDataDto>(), MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData), Times.Once);
    }
    [Fact]
    public void NativeDetailsDoNotRequireOrContactSeerrEvenWhenTheItemHasAnExternalId()
    {
        settings.Update(new(settings.Current.Revision, false, "", [], "", null, ""));
        var movie = new Mock<Movie>(); movie.Object.Id = Guid.NewGuid(); movie.Object.Name = "Native title"; movie.Object.ProviderIds["Tmdb"] = "123";
        movie.Setup(m => m.IsVisibleStandalone(viewer)).Returns(true); library.Setup(l => l.GetItemById(movie.Object.Id)).Returns(movie.Object);
        var card = catalog.NativeDetails(viewer, movie.Object.Id); Assert.True(card.NativeOnly); Assert.Equal(123, card.ExternalId); Assert.Empty(server.Calls); data.VerifyNoOtherCalls();
        Assert.NotNull(catalog.NativeDetails(viewer, movie.Object.Id, true).Activity); Assert.Empty(server.Calls);
        data.Verify(d => d.SaveUserData(viewer, movie.Object, It.IsAny<MediaBrowser.Model.Dto.UpdateUserItemDataDto>(), MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData), Times.Once);
    }
    [Fact]
    public async Task RestrictedNativeMediaIsOmittedFromCatalogRequestsAndDetails()
    {
        var movie = new Mock<Movie>(); movie.Object.Id = Guid.NewGuid(); movie.Object.ProviderIds["Tmdb"] = "123";
        movie.Setup(m => m.IsVisibleStandalone(viewer)).Returns(false);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie.Object]);
        Assert.Empty((await catalog.Browse(viewer, "movie", "", 1, CancellationToken.None)).Items);
        Assert.Empty((await catalog.Requests(viewer, false, 1, CancellationToken.None)).Items);
        await Assert.ThrowsAsync<HubError>(() => catalog.Details(viewer, "movie", 123, CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None));
        data.VerifyNoOtherCalls(); Assert.Equal(0, server.Writes);
    }
    [Theory]
    [InlineData("payload")] [InlineData("media")] [InlineData("expiry")]
    public async Task TamperedRetainedRequestCannotSendAnyExternalWrite(string change)
    {
        var plan = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        using (var db = store.Open()) using (var update = HubStore.Command(db, change switch
        { "payload" => "UPDATE request_operations SET payload='{}' WHERE id=$0", "media" => "UPDATE request_operations SET external_id=999 WHERE id=$0", _ => "UPDATE request_operations SET expires='2099-01-01T00:00:00.0000000+00:00' WHERE id=$0" }, plan.Id)) update.ExecuteNonQuery();
        await Assert.ThrowsAnyAsync<Exception>(() => requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None));
        Assert.Equal(0, server.Writes); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task NegativePermissionFlagsCannotActAsAnAdministrator()
    { server.Permissions = -1; await Assert.ThrowsAsync<HubError>(() => bridge.Open(viewer, CancellationToken.None)); Assert.Equal(0, server.Writes); }
    [Fact]
    public async Task ConfirmedRequestsAndDetailsReportPartialNativeActivityFailureWithoutReplay()
    {
        var movie = new Mock<Movie>(); movie.Object.Id = Guid.NewGuid(); movie.Object.ProviderIds["Tmdb"] = "123";
        movie.Setup(m => m.IsVisibleStandalone(viewer)).Returns(true);
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([movie.Object]);
        library.Setup(l => l.GetItemById(movie.Object.Id)).Returns(movie.Object);
        data.Setup(d => d.SaveUserData(viewer, movie.Object, It.IsAny<MediaBrowser.Model.Dto.UpdateUserItemDataDto>(), MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData)).Throws(new IOException());
        var card = await catalog.Details(viewer, "movie", 123, CancellationToken.None, true);
        Assert.False(card.Activity!.JellyfinUpdated); Assert.True(card.Activity.LocalRecorded);
        var plan = await requests.Prepare(viewer, "movie", 123, false, null, CancellationToken.None);
        var receipt = await requests.Confirm(viewer, plan.Id, () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("completed", receipt.State); Assert.False(receipt.Activity!.JellyfinUpdated); Assert.True(receipt.Activity.LocalRecorded);
        _ = requests.Receipt(viewer, plan.Id);
        data.Verify(d => d.SaveUserData(viewer, movie.Object, It.IsAny<MediaBrowser.Model.Dto.UpdateUserItemDataDto>(), MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserData), Times.Exactly(2));
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT COUNT(*) FROM activity"); Assert.Equal(1L, read.ExecuteScalar()); Assert.Equal(1, server.Writes);
    }
    [Fact]
    public async Task WrongOwnerRequestPageAndInvalidSeasonSelectionAreRejected()
    {
        server.WrongOwner = true;
        await Assert.ThrowsAsync<HubError>(() => catalog.Requests(viewer, false, 1, CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => catalog.Requests(viewer, true, 1, CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => requests.Prepare(viewer, "tv", 123, false, [99], CancellationToken.None));
        Assert.Equal(0, server.Writes); data.VerifyNoOtherCalls();
    }
    private WatchlistSync WatchSync()
    {
        var media = new Mock<IExternalMedia>();
        media.Setup(m => m.Resolve(viewer, It.IsAny<MediaKey>())).Returns((true, (Guid?)null));
        return new(store, bridge, media.Object, settings: settings);
    }
    [Fact]
    public async Task WatchlistUnionsFirstSyncThenRespectsRemoteAndExplicitLocalRemovals()
    {
        var sync = WatchSync(); server.Remote.Add(new("movie", 456), "Another title");
        await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, CancellationToken.None);
        Assert.True((await sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None)).Complete);
        Assert.Equal(2, sync.List(viewer).Count); Assert.Equal(2, server.Remote.Count);
        Assert.False(sync.List(viewer).Single(w => w.ExternalId == 456).Subscribed);
        server.Remote.Remove(new("movie", 456));
        await sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None);
        Assert.Single(sync.List(viewer));
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT revision FROM personal WHERE user=$0", viewer.Id.ToString("N")))
            await sync.Follow(viewer, new("movie", 123), null, (long)read.ExecuteScalar()!, () => Task.CompletedTask, CancellationToken.None);
        await sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None);
        Assert.Empty(sync.List(viewer)); Assert.Empty(server.Remote);
        Assert.All(server.Calls.Where(c => c.Path == "watchlist" || c.Path.StartsWith("watchlist/")), c => Assert.Equal("7", c.User));
        data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task PartialWatchlistReadsCannotRemoveSavedTitlesOrSendWrites()
    {
        var sync = WatchSync();
        await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, CancellationToken.None);
        server.IncompleteWatch = true;
        await Assert.ThrowsAsync<HubError>(() => sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None));
        Assert.Single(sync.List(viewer)); Assert.Equal(0, server.Writes);
    }
    [Fact]
    public async Task ConcurrentServiceInstancesCannotOverlapAWatchlistSyncOrSendTheSameIntentTwice()
    {
        var sync = WatchSync(); await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeWatchRead = () => { entered.TrySetResult(); return release.Task; };
        var first = sync.Sync(viewer, () => Task.CompletedTask, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { await Assert.ThrowsAnyAsync<Exception>(() => WatchSync().Sync(viewer, () => Task.CompletedTask, default)); }
        finally { release.TrySetResult(); }
        Assert.True((await first).Complete); Assert.Equal(1, server.Writes); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task FailedWatchlistMutationIsReconciledButNotResentWithoutNewExplicitIntent()
    {
        var sync = WatchSync();
        await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, CancellationToken.None);
        server.FailWatch = true;
        Assert.False((await sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None)).Complete);
        Assert.False((await WatchSync().Sync(viewer, () => Task.CompletedTask, CancellationToken.None)).Complete);
        Assert.Equal(1, server.Writes);
        server.FailWatch = false;
        await sync.Follow(viewer, new("movie", 123), true, 1, () => Task.CompletedTask, CancellationToken.None);
        Assert.True((await sync.Sync(viewer, () => Task.CompletedTask, CancellationToken.None)).Complete); Assert.Equal(2, server.Writes);
    }
    [Fact]
    public async Task ForgedReadyIntentCannotWriteWithoutAnExplicitFollowEvenAfterRestart()
    {
        var sync = WatchSync();
        using (var db = store.Open()) using (var inject = HubStore.Command(db, "INSERT INTO watchlist_intents VALUES($0,'movie',123,1,0,'ready')", viewer.Id.ToString("N"))) inject.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => sync.Sync(viewer, () => Task.CompletedTask, default));
        await Assert.ThrowsAsync<HubError>(() => WatchSync().Sync(viewer, () => Task.CompletedTask, default));
        Assert.Equal(0, server.Writes);
    }
    [Theory]
    [InlineData("DELETE FROM watchlist_intents")]
    [InlineData("UPDATE watchlist_intents SET desired=0")]
    [InlineData("INSERT INTO watchlist_baselines VALUES($0,'[]')")]
    [InlineData("DELETE FROM external_watches")]
    public async Task MutatedWatchAuthorizersFailClosedWithoutResealingOrSending(string sql)
    {
        var sync = WatchSync(); await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, default);
        using (var db = store.Open()) using (var inject = HubStore.Command(db, sql, viewer.Id.ToString("N"))) inject.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => sync.Sync(viewer, () => Task.CompletedTask, default));
        await Assert.ThrowsAsync<HubError>(() => sync.Follow(viewer, new("movie", 123), true, 1, () => Task.CompletedTask, default));
        Assert.Equal(0, server.Writes);
    }
    [Fact]
    public async Task UnsubscribeKeepsMembershipAndStaleRevisionCannotRemoveIt()
    {
        var sync = WatchSync();
        await sync.Follow(viewer, new("tv", 123), true, 0, () => Task.CompletedTask, CancellationToken.None);
        await sync.Follow(viewer, new("tv", 123), false, 1, () => Task.CompletedTask, CancellationToken.None);
        Assert.False(Assert.Single(sync.List(viewer)).Subscribed);
        await Assert.ThrowsAsync<HubError>(() => sync.Follow(viewer, new("tv", 123), null, 1, () => Task.CompletedTask, CancellationToken.None));
        Assert.Single(sync.List(viewer)); Assert.Equal(0, server.Writes);
    }
    private (WatchlistSync Sync, PersonalService Personal, Guid[] Aliases) AliasFixture()
    {
        var aliases = new[] { Guid.NewGuid(), Guid.NewGuid() }; var mapping = new Mock<IExternalMedia>(); var visible = new Mock<IViewerMedia>();
        foreach (var id in aliases)
        {
            mapping.Setup(m => m.Identify(viewer, id)).Returns((new MediaKey("movie", 123), "A title"));
            visible.Setup(m => m.Find(viewer, id)).Returns(new VisibleItem(id, "A title", "Movie"));
        }
        mapping.Setup(m => m.Resolve(viewer, It.IsAny<MediaKey>())).Returns((true, (Guid?)null));
        var sync = new WatchlistSync(store, bridge, mapping.Object, settings: settings);
        return (sync, new PersonalService(store, visible.Object, sync: sync), aliases);
    }
    [Fact]
    public async Task ExplicitSyncImportsLegacyAliasesOnceWithoutChangingNotificationChoicesOrNativeData()
    {
        var (sync, personal, aliases) = AliasFixture();
        settings.Protection.Update("watches", (db, tx) => { foreach (var id in aliases) { using var add = HubStore.Command(db, "INSERT INTO subscriptions VALUES($0,$1,0,$2)", viewer.Id.ToString("N"), id.ToString("N"), HubStore.Now); add.Transaction = tx; add.ExecuteNonQuery(); } return true; });
        await sync.Sync(viewer, () => Task.CompletedTask, default, importNative: false); Assert.Empty(sync.List(viewer)); Assert.Equal(0, server.Writes);
        await sync.Sync(viewer, () => Task.CompletedTask, default); var row = Assert.Single(sync.List(viewer));
        Assert.False(row.Subscribed); Assert.Null(row.NativeId); Assert.Equal(2, row.NativeIds!.Count); Assert.Equal(1, server.Writes);
        await sync.Sync(viewer, () => Task.CompletedTask, default); Assert.Equal(1, server.Writes); Assert.Equal(2, personal.State(viewer).Watchlist.Count);
        data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task RemoteRemovalClearsEveryAccessibleSavedAliasAndItsNotifications()
    {
        var (sync, personal, aliases) = AliasFixture();
        personal.Bell(viewer, aliases[0], true, 0); personal.Bell(viewer, aliases[1], true, 1);
        await sync.Sync(viewer, () => Task.CompletedTask, default);
        server.Remote.Clear(); await sync.Sync(viewer, () => Task.CompletedTask, default);
        Assert.Empty(personal.State(viewer).Watchlist); Assert.Empty(sync.List(viewer)); Assert.Equal(1, server.Writes);
        using var db = store.Open(); using var count = HubStore.Command(db, "SELECT COUNT(*) FROM subscriptions"); Assert.Equal(0L, count.ExecuteScalar()); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task NativeUnsubscribeAndRemoveApplyToAllSavedEditionsWithoutTouchingOtherUsers()
    {
        var (sync, personal, aliases) = AliasFixture();
        personal.Bell(viewer, aliases[0], true, 0); personal.Bell(viewer, aliases[1], true, 1);
        personal.Bell(viewer, aliases[0], false, 2); Assert.All(personal.State(viewer).Watchlist, w => Assert.False(w.Subscribed)); Assert.False(Assert.Single(sync.List(viewer)).Subscribed);
        settings.Protection.Update("watches", (db, tx) => { using var add = HubStore.Command(db, "INSERT INTO subscriptions VALUES('other',$0,1,$1)", aliases[0].ToString("N"), HubStore.Now); add.Transaction = tx; return add.ExecuteNonQuery(); });
        personal.Remove(viewer, aliases[0], 3); Assert.Empty(personal.State(viewer).Watchlist); Assert.Empty(sync.List(viewer));
        using (var db = store.Open()) using (var count = HubStore.Command(db, "SELECT COUNT(*) FROM subscriptions WHERE user='other'")) Assert.Equal(1L, count.ExecuteScalar());
        Assert.Equal(0, server.Writes); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task LocalMutationDuringStableRemoteScanCannotBeOverwrittenByLegacyImport()
    {
        var (sync, personal, aliases) = AliasFixture(); personal.Bell(viewer, aliases[0], true, 0);
        await Assert.ThrowsAsync<HubError>(() => sync.Sync(viewer, () => { personal.Bell(viewer, aliases[0], false, 1); return Task.CompletedTask; }, default));
        Assert.False(Assert.Single(personal.State(viewer).Watchlist).Subscribed); Assert.Equal(0, server.Writes);
    }
    [Fact]
    public async Task ReviewedUncertainRecoveryAdoptsRemoteOnlyAndRejectsReplayWithoutAnyRetry()
    {
        var sync = WatchSync(); await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, default);
        server.FailWatch = true; await sync.Sync(viewer, () => Task.CompletedTask, default); Assert.Equal(1, server.Writes);
        var review = await sync.Review(viewer, new("movie", 123), () => Task.CompletedTask, default); Assert.False(review.RemoteMembership); Assert.True(review.LocalMembership);
        Assert.True((await sync.AcceptRemote(viewer, review.Token, () => Task.CompletedTask, default)).Complete);
        Assert.Empty(sync.List(viewer)); Assert.Empty(sync.Issues(viewer)); Assert.Equal(1, server.Writes);
        await Assert.ThrowsAsync<HubError>(() => sync.AcceptRemote(viewer, review.Token, () => Task.CompletedTask, default)); Assert.Equal(1, server.Writes);
    }
    [Theory]
    [InlineData("wrong-user")] [InlineData("remote-change")] [InlineData("draft-change")] [InlineData("tamper")]
    public async Task ChangedOrForgedRecoveryProofCannotDiscardPendingState(string change)
    {
        var sync = WatchSync(); await sync.Follow(viewer, new("movie", 123), true, 0, () => Task.CompletedTask, default);
        server.FailWatch = true; await sync.Sync(viewer, () => Task.CompletedTask, default);
        var review = await sync.Review(viewer, new("movie", 123), () => Task.CompletedTask, default);
        var actor = change == "wrong-user" ? new User("Other", "provider", "reset") { Id = Guid.NewGuid() } : viewer;
        if (change == "remote-change") server.Remote.Add(new("movie", 123), "A title");
        if (change == "draft-change") await sync.Follow(viewer, new("movie", 123), false, 1, () => Task.CompletedTask, default);
        await Assert.ThrowsAsync<HubError>(() => sync.AcceptRemote(actor, change == "tamper" ? "tampered-proof" : review.Token, () => Task.CompletedTask, default));
        Assert.NotEmpty(sync.Issues(viewer)); Assert.Equal(1, server.Writes); data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ExistingOwnRequestsNotifyWithoutWatchlistMembershipAndOwnershipLossHidesRetainedNotices()
    {
        var personal = new PersonalService(store, Mock.Of<IViewerMedia>());
        var feed = new NotificationFeed(store, personal, Mock.Of<INotificationStatus>(), identity, settings, catalog);
        await feed.Scan(viewer, default); Assert.Empty(await feed.ListAsync(viewer, default));
        server.RequestStatus = 3; await feed.Scan(viewer, default);
        var notice = Assert.Single(await feed.ListAsync(viewer, default)); Assert.Equal("Declined", notice.Status); Assert.StartsWith("Your request", notice.Title);
        Assert.Empty(personal.State(viewer).Watchlist); Assert.Equal(0, server.Writes); data.VerifyNoOtherCalls();
        server.WrongOwner = true; Assert.Empty(await feed.ListAsync(viewer, default));
        server.WrongOwner = false; personal.Save(viewer, 0, new(OwnRequestNotifications: false)); Assert.Empty(await feed.ListAsync(viewer, default));
    }
    private sealed class Handler(Server server) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => server.Send(request, cancellation);
    }
    private sealed class Server(Guid native)
    {
        public int UserId = 7, Writes, RequestStatus = 2; public long Permissions = 32;
        public bool Duplicate, IgnoreUser, Incomplete, LoseResponse, WrongOwner, IncompleteWatch, FailWatch;
        public readonly Dictionary<MediaKey, string> Remote = [];
        public readonly List<(string Path, string? User)> Calls = [];
        public readonly List<string> Bodies = [];
        public Func<Task>? BeforeWatchRead;
        public async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.PathAndQuery["/base/api/v1/".Length..];
            if (path.StartsWith("discover/watchlist?") && BeforeWatchRead is not null) await BeforeWatchRead();
            var apiUser = request.Headers.TryGetValues("X-API-User", out var values) ? values.Single() : null;
            Calls.Add((path, apiUser)); Assert.Equal("key", request.Headers.GetValues("X-Api-Key").Single());
            object body;
            object Request() => new { id = 22, type = "movie", is4k = false, requestedBy = new { id = WrongOwner ? 999 : UserId }, media = new { tmdbId = 123 }, status = RequestStatus };
            if (path == "discover/watchlist?page=1") body = new { page = 1, totalResults = IncompleteWatch ? 1 : Remote.Count, totalPages = 1,
                results = Remote.Select(pair => new { mediaType = pair.Key.Kind, tmdbId = pair.Key.Id, title = pair.Value, requestedBy = new { id = UserId } }).ToArray() };
            else if (path == "watchlist" || path.StartsWith("watchlist/"))
            {
                Writes++; Assert.Equal(UserId.ToString(), apiUser); Assert.Equal("csrf", request.Headers.GetValues("X-XSRF-Token").Single());
                if (FailWatch) throw new HttpRequestException("Outcome unavailable");
                if (request.Method == HttpMethod.Post)
                {
                    var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellation))!;
                    Remote[new(json["mediaType"]!.GetValue<string>(), json["tmdbId"]!.GetValue<int>())] = json["title"]!.GetValue<string>();
                }
                else { var parts = path.Split('?'); Remote.Remove(new(parts[1]["mediaType=".Length..], int.Parse(parts[0]["watchlist/".Length..]))); }
                body = new { };
            }
            else if (request.Method == HttpMethod.Post)
            {
                Writes++; Assert.Equal(UserId.ToString(), apiUser); Assert.Equal("csrf", request.Headers.GetValues("X-XSRF-Token").Single());
                var text = await request.Content!.ReadAsStringAsync(cancellation); Bodies.Add(text); var json = JsonNode.Parse(text)!;
                body = new { id = 22, type = json["mediaType"]!.GetValue<string>(), is4k = json["is4k"]!.GetValue<bool>(), requestedBy = new { id = UserId }, media = new { tmdbId = json["mediaId"]!.GetValue<int>() } };
                if (LoseResponse) throw new HttpRequestException("Applied, but response lost");
            }
            else if (path.StartsWith("user?")) body = new { pageInfo = new { results = Incomplete ? 99 : Duplicate ? 2 : 1 }, results = Duplicate ? new[] { new { id = UserId, jellyfinUserId = native.ToString() }, new { id = 9, jellyfinUserId = native.ToString() } } : [new { id = UserId, jellyfinUserId = native.ToString() }] };
            else if (path == "auth/me") body = new { id = IgnoreUser ? 1 : UserId, permissions = Permissions };
            else if (path == "user/" + UserId) body = new { id = UserId, jellyfinUserId = native.ToString() };
            else if (path.StartsWith("request?")) body = new { pageInfo = new { results = 1 }, results = new[] { Request() } };
            else if (path == "request/22") body = Request();
            else if (path.StartsWith("search?") || path.StartsWith("discover/")) body = new { totalPages = 1, results = new[] { new { id = 123, mediaType = "movie", title = "A title" } } };
            else if (path == "settings/public") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { }), Headers = { { "Set-Cookie", "XSRF-TOKEN=csrf; Path=/" } } };
            else body = new { id = 123, title = "A title", mediaInfo = new { status = 5 }, seasons = new[] { new { seasonNumber = 1 } } };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
