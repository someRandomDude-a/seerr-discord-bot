using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class ConsentDeliveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-dm-" + Guid.NewGuid());
    private readonly User recipient = new("Viewer", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly User owner = new("Owner", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly PersonalService personal;
    private readonly DeletionQueue queue;
    private readonly HostIdentity identity;
    private readonly Mock<ISessionManager> sessions = new();
    private readonly Guid item = Guid.NewGuid();
    public ConsentDeliveryTests()
    {
        store = new(directory); settings = new(store); settings.Update(new(0, true, "https://jellyfin.example", ["555"], "", "bot-token", null));
        var users = new Mock<IUserManager>(); users.Setup(u => u.GetUserById(recipient.Id)).Returns(recipient);
        var session = new SessionInfo(sessions.Object, NullLogger.Instance) { Id = "browser", UserId = recipient.Id, DeviceId = "device" };
        sessions.Setup(s => s.GetSessionByAuthenticationToken("user-token", "device", It.IsAny<string>())).ReturnsAsync(session);
        var code = store.BeginLink("42"); store.Approve(code, recipient.Id, "42", settings.ProtectLink("user-token", "42", recipient.Id, "device"), "device");
        identity = new(store, settings, users.Object, sessions.Object);
        var media = new Mock<IViewerMedia>(); media.Setup(m => m.Find(It.IsAny<User>(), item)).Returns(new VisibleItem(item, "Title", "Movie"));
        personal = new(store, media.Object);
        var activity = new Mock<IRecentActivity>(); activity.Setup(a => a.Read(item)).Returns([new(recipient.Id, DateTimeOffset.UtcNow.AddDays(-1))]);
        var authority = new Mock<IRequestAuthority>(); authority.Setup(a => a.Verify(owner, 1, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "private"));
        queue = new(store, settings, personal, media.Object, activity.Object, authority.Object);
    }
    private ConsentDelivery Delivery() => new(store, settings, identity, personal, queue);
    [Fact]
    public async Task RequiresPersonalOptInAndMutePreventsSending()
    {
        await queue.Propose(owner, 1, item, CancellationToken.None); var count = 0;
        Task Send(string id, ConsentNotice notice, CancellationToken token) { count++; return Task.CompletedTask; }
        await Delivery().Deliver(Send, CancellationToken.None); Assert.Equal(0, count);
        personal.Save(recipient, 0, new(DiscordNotifications: true, Muted: true));
        await Delivery().Deliver(Send, CancellationToken.None); Assert.Equal(0, count);
        personal.Save(recipient, 1, new(DiscordNotifications: true));
        await Delivery().Deliver(Send, CancellationToken.None); Assert.Equal(1, count);
        await Delivery().Deliver(Send, CancellationToken.None); Assert.Equal(1, count);
    }
    [Fact]
    public async Task AmbiguousDmIsNotAutomaticallyResentAfterRestart()
    {
        personal.Save(recipient, 0, new(DiscordNotifications: true)); await queue.Propose(owner, 1, item, CancellationToken.None);
        var count = 0;
        Task Send(string id, ConsentNotice notice, CancellationToken token) { count++; Assert.Equal("42", id); throw new TimeoutException(); }
        await Delivery().Deliver(Send, CancellationToken.None); await Delivery().Deliver(Send, CancellationToken.None);
        Assert.Equal(1, count); Assert.Single(queue.Notices(recipient));
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT state FROM consent_delivery"); Assert.Equal("uncertain", read.ExecuteScalar());
    }
    [Fact]
    public async Task FailedLiveVerificationDoesNotConsumeDeliveryIntent()
    {
        personal.Save(recipient, 0, new(DiscordNotifications: true)); await queue.Propose(owner, 1, item, CancellationToken.None);
        sessions.Setup(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new IOException());
        await Delivery().Deliver((_, _, _) => throw new Exception("Must not send"), CancellationToken.None);
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT COUNT(*) FROM consent_delivery"); Assert.Equal(0L, read.ExecuteScalar());
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
