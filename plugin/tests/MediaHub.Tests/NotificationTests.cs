using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-notifications-" + Guid.NewGuid());
    private readonly User user = new("Viewer", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly Guid item = Guid.NewGuid();
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly HostIdentity identity;
    private readonly PersonalService personal;
    private readonly Mock<IViewerMedia> media = new();
    private readonly Mock<INotificationStatus> statuses = new();
    private string status = "Downloading";
    public NotificationTests()
    {
        store = new(directory); settings = new(store); settings.Update(new(0, true, "https://jellyfin.example", ["555"], "", "bot-token", null));
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var sessions = new Mock<ISessionManager>(); var session = new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "browser", UserId = user.Id, DeviceId = "device" };
        sessions.Setup(s => s.GetSessionByAuthenticationToken("token", "device", It.IsAny<string>())).ReturnsAsync(session);
        var code = store.BeginLink("42"); store.Approve(code, user.Id, "42", settings.ProtectLink("token", "42", user.Id, "device"), "device");
        identity = new(store, settings, users.Object, sessions.Object);
        media.Setup(m => m.Find(user, item)).Returns(new VisibleItem(item, "A title", "Movie"));
        personal = new(store, media.Object); personal.Bell(user, item, true, 0);
        statuses.Setup(s => s.Read(user, item.ToString("N"), It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
    }
    private NotificationFeed Feed() => new(store, personal, statuses.Object, identity, settings);
    [Fact]
    public async Task InitialAndRepeatedStatusesAreSilentAndFeedIsPrivateAndDurable()
    {
        var feed = Feed(); await feed.Scan(user, CancellationToken.None); Assert.Empty(feed.List(user));
        status = "Available"; await feed.Scan(user, CancellationToken.None);
        var notice = Assert.Single(feed.List(user)); Assert.Equal("Available", notice.Status);
        await feed.Scan(user, CancellationToken.None); Assert.Single(Feed().List(user));
        var other = new User("Other", "provider", "reset") { Id = Guid.NewGuid() };
        feed.Seen(other, notice.Id); Assert.False(Assert.Single(feed.List(user)).Seen);
        feed.Seen(user, notice.Id); Assert.True(Assert.Single(Feed().List(user)).Seen); Assert.Empty(feed.List(other));
        media.Setup(m => m.Find(user, item)).Returns((VisibleItem?)null); Assert.Empty(feed.List(user));
    }
    [Fact]
    public async Task MuteAndUnsubscribeSuppressNotificationsWithoutRemovingWatchlist()
    {
        var feed = Feed(); await feed.Scan(user, CancellationToken.None);
        personal.Save(user, 1, new(Muted: true)); status = "Available"; await feed.Scan(user, CancellationToken.None);
        Assert.Empty(feed.List(user)); Assert.Single(personal.State(user).Watchlist);
        personal.Save(user, 2, new()); await feed.Scan(user, CancellationToken.None); Assert.Empty(feed.List(user));
        personal.Bell(user, item, false, 3); status = "Downloading"; await feed.Scan(user, CancellationToken.None); Assert.Empty(feed.List(user));
        Assert.Single(personal.State(user).Watchlist);
    }
    [Fact]
    public async Task DiscordRequiresOptInAndAmbiguousSendIsNotRepeatedAfterRestart()
    {
        personal.Save(user, 1, new(DiscordNotifications: true));
        var feed = Feed(); await feed.Scan(user, CancellationToken.None); status = "Available"; await feed.Scan(user, CancellationToken.None);
        var sends = 0;
        Task Send(string discord, HubNotification notice, CancellationToken cancellation) { Assert.Equal("42", discord); sends++; throw new TimeoutException(); }
        await feed.Deliver(Send, CancellationToken.None); await Feed().Deliver(Send, CancellationToken.None); Assert.Equal(1, sends);
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT delivery FROM media_notifications"); Assert.Equal("uncertain", read.ExecuteScalar());
    }
    [Fact]
    public async Task RevokedAccessAndReadOutagesNeverBecomeAvailabilityChanges()
    {
        var feed = Feed(); await feed.Scan(user, CancellationToken.None);
        statuses.Setup(s => s.Read(user, item.ToString("N"), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException());
        await Assert.ThrowsAsync<IOException>(() => feed.Scan(user, CancellationToken.None)); Assert.Empty(feed.List(user));
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT status FROM notification_baselines"); Assert.Equal("Downloading", read.ExecuteScalar());
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
