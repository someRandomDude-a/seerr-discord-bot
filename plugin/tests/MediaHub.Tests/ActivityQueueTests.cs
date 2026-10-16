using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class ActivityQueueTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-queue-" + Guid.NewGuid());
    private readonly User owner = new("Owner", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly User other = new("Other", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly Guid item = Guid.NewGuid();
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly Mock<IViewerMedia> media = new();
    private readonly Mock<IRecentActivity> activity = new();
    private readonly Mock<IRequestAuthority> authority = new();
    private IReadOnlyList<RecentViewer> viewers = [];
    private readonly DeletionQueue queue;
    public ActivityQueueTests()
    {
        store = new(directory); settings = new(store);
        media.Setup(m => m.Find(It.IsAny<User>(), item)).Returns(new VisibleItem(item, "A title", "Movie"));
        authority.Setup(a => a.Verify(owner, 22, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "private-origin-and-destination"));
        activity.Setup(a => a.Read(item)).Returns(() => viewers);
        queue = NewQueue();
    }
    private DeletionQueue NewQueue() => new(new HubStore(directory), settings, new PersonalService(store, media.Object), media.Object, activity.Object, authority.Object);
    private Task Propose() => queue.Propose(owner, 22, item, CancellationToken.None);
    [Fact]
    public async Task QueueAndAnonymousProjectionsSurviveRestart()
    {
        await Propose(); var own = Assert.Single(queue.List(owner)); var anonymous = Assert.Single(NewQueue().List(other));
        Assert.Equal("queued", own.State); Assert.True(own.CanUndo); Assert.Equal("Yours", own.Owner);
        Assert.Equal("Anonymous", anonymous.Owner); Assert.False(anonymous.CanUndo);
        var json = JsonSerializer.Serialize(anonymous);
        Assert.DoesNotContain(owner.Id.ToString(), json); Assert.DoesNotContain(owner.Id.ToString("N"), json);
        Assert.DoesNotContain("private-origin", json); Assert.DoesNotContain("RequestId", json);
    }
    [Fact]
    public async Task RecentViewersGetPrivateDurableConsentAndMustAllAgree()
    {
        viewers = [new(owner.Id, DateTimeOffset.UtcNow.AddDays(-1)), new(other.Id, DateTimeOffset.UtcNow.AddDays(-1))];
        await Propose(); Assert.Equal("waiting", Assert.Single(queue.List(owner)).State);
        Assert.Empty(queue.Notices(owner)); var notice = Assert.Single(NewQueue().Notices(other));
        await queue.Respond(other, notice.Id, true, CancellationToken.None);
        await queue.Refresh(owner, CancellationToken.None);
        Assert.Equal("queued", Assert.Single(queue.List(owner)).State);
        Assert.Empty(queue.Notices(other));
    }
    [Fact]
    public async Task DeclineBlocksUntilViewerAgesOutThenStartsFullDelay()
    {
        viewers = [new(other.Id, DateTimeOffset.UtcNow.AddDays(-1))]; await Propose();
        await queue.Respond(other, Assert.Single(queue.Notices(other)).Id, false, CancellationToken.None);
        await queue.Refresh(owner, CancellationToken.None); Assert.Equal("waiting", Assert.Single(queue.List(owner)).State);
        viewers = [new(other.Id, DateTimeOffset.UtcNow.AddDays(-15))];
        await queue.Refresh(owner, CancellationToken.None);
        var row = Assert.Single(queue.List(owner)); Assert.Equal("queued", row.State);
        Assert.True(row.DueAt > DateTimeOffset.UtcNow.AddHours(47));
    }
    [Fact]
    public async Task NewActivityBlocksOldConsentAndRequiresFreshNotice()
    {
        viewers = [new(other.Id, DateTimeOffset.UtcNow.AddDays(-1))]; await Propose();
        var old = Assert.Single(queue.Notices(other)); await queue.Respond(other, old.Id, true, CancellationToken.None);
        await queue.Refresh(owner, CancellationToken.None); Assert.Equal("queued", Assert.Single(queue.List(owner)).State);
        viewers = [new(other.Id, DateTimeOffset.UtcNow)]; await queue.Refresh(owner, CancellationToken.None);
        Assert.Equal("waiting", Assert.Single(queue.List(owner)).State);
        Assert.NotEqual(old.Id, Assert.Single(queue.Notices(other)).Id);
        await Assert.ThrowsAsync<HubError>(() => queue.Respond(other, old.Id, true, CancellationToken.None));
    }
    [Fact]
    public async Task NoticeCannotBeUsedByAnotherAccountAndReplayDoesNotResetDeadline()
    {
        viewers = [new(other.Id, DateTimeOffset.UtcNow.AddDays(-1))]; await Propose();
        var notice = Assert.Single(queue.Notices(other));
        await Assert.ThrowsAsync<HubError>(() => queue.Respond(owner, notice.Id, true, CancellationToken.None));
        await queue.Respond(other, notice.Id, true, CancellationToken.None); await queue.Refresh(owner, CancellationToken.None);
        var before = Assert.Single(queue.List(owner));
        await queue.Respond(other, notice.Id, true, CancellationToken.None);
        Assert.Equal(before, Assert.Single(queue.List(owner)));
    }
    [Fact]
    public async Task OutageBlocksAndOwnerCanUndoWithoutUpstreamApproval()
    {
        await Propose(); var row = Assert.Single(queue.List(owner));
        authority.Setup(a => a.Verify(owner, 22, item, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await queue.Refresh(owner, CancellationToken.None);
        Assert.Equal("blocked", Assert.Single(queue.List(owner)).State); Assert.Null(Assert.Single(queue.List(owner)).DueAt);
        Assert.Throws<HubError>(() => queue.Undo(other, row.Id)); queue.Undo(owner, row.Id);
        Assert.Empty(queue.List(owner));
    }
    [Fact]
    public async Task IncreasingPersonalDelayExtendsExistingQueueWithoutLaterShorteningIt()
    {
        await Propose();
        var personal = new PersonalService(store, media.Object);
        personal.Save(owner, 0, new(DeletionDelayDays: 14));
        await queue.Refresh(owner, CancellationToken.None);
        var extended = Assert.Single(queue.List(owner)).DueAt;
        Assert.True(extended > DateTimeOffset.UtcNow.AddDays(13));
        personal.Save(owner, 1, new(DeletionDelayDays: 2)); await queue.Refresh(owner, CancellationToken.None);
        Assert.Equal(extended, Assert.Single(queue.List(owner)).DueAt);
    }
    [Fact]
    public async Task HiddenMediaCannotLeakThroughQueueOrNoticesAndRepeatedReadsDoNotCreateActivity()
    {
        viewers = [new(other.Id, DateTimeOffset.UtcNow.AddDays(-1))]; await Propose();
        media.Setup(m => m.Find(other, item)).Returns((VisibleItem?)null);
        Assert.Empty(queue.List(other)); Assert.Empty(queue.Notices(other));
        using var db = store.Open(); using var count = HubStore.Command(db, "SELECT COUNT(*) FROM activity"); Assert.Equal(0L, count.ExecuteScalar());
    }
    [Fact]
    public void NativeActivityUsesOnlyTimestampPatchAndRetainsJournalOnHostFailure()
    {
        var library = new Mock<ILibraryManager>(); var users = new Mock<IUserManager>(); var data = new Mock<IUserDataManager>();
        var movie = new Mock<Movie>(); movie.Object.Id = item; movie.Setup(m => m.IsVisibleStandalone(owner)).Returns(true);
        library.Setup(l => l.GetItemById(item)).Returns(movie.Object); users.Setup(u => u.GetUsers()).Returns([owner]);
        var original = new UserItemData { Key = "item", Played = true, PlaybackPositionTicks = 123, PlayCount = 4, IsFavorite = true };
        data.Setup(d => d.GetUserData(owner, movie.Object)).Returns(original);
        data.Setup(d => d.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), owner)).Returns(new Dictionary<Guid, UserItemData> { [item] = original });
        var service = new MediaActivity(store, library.Object, users.Object, data.Object);
        Assert.True(service.Record(owner, item, MediaInteraction.Details).JellyfinUpdated);
        data.Verify(d => d.SaveUserData(owner, movie.Object, It.Is<UpdateUserItemDataDto>(patch => patch.LastPlayedDate != null && patch.Played == null && patch.PlayCount == null && patch.PlaybackPositionTicks == null && patch.IsFavorite == null && patch.Rating == null && patch.Likes == null), UserDataSaveReason.UpdateUserData), Times.Once);
        Assert.True(original.Played); Assert.Equal(123, original.PlaybackPositionTicks); Assert.Equal(4, original.PlayCount);
        data.Setup(d => d.SaveUserData(owner, movie.Object, It.IsAny<UpdateUserItemDataDto>(), UserDataSaveReason.UpdateUserData)).Throws<IOException>();
        Assert.False(service.Record(owner, item, MediaInteraction.Open).JellyfinUpdated);
        Assert.Single(service.Read(item));
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LiveAuthorityRequiresOriginalSeerrOwnersNativeJellyfinIdentity(bool owns)
    {
        settings.Update(new(0, false, "", [], "https://seerr.example", null, "api-secret"));
        var library = new Mock<ILibraryManager>(); var movie = new Mock<Movie>(); movie.SetupAllProperties();
        movie.Object.Id = item; movie.Object.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" };
        movie.Setup(m => m.IsVisibleStandalone(owner)).Returns(true); library.Setup(l => l.GetItemById(item)).Returns(movie.Object);
        var handler = new AuthorityHandler(request =>
        {
            Assert.Equal("api-secret", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            object body = request.RequestUri!.AbsolutePath.EndsWith("/request/22")
                ? new { id = 22, status = 2, type = "movie", is4k = false, requestedBy = new { id = 7 }, media = new { id = 11, tmdbId = 123, serviceId = 1 } }
                : new { id = 7, jellyfinUserId = (owns ? owner.Id : other.Id).ToString() };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        });
        using var live = new RequestAuthority(settings, library.Object, handler);
        if (owns) Assert.Equal(item, (await live.Verify(owner, 22, item, CancellationToken.None)).Item);
        else await Assert.ThrowsAsync<HubError>(() => live.Verify(owner, 22, item, CancellationToken.None));
        Assert.Equal(2, handler.Calls);
    }
    private sealed class AuthorityHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
