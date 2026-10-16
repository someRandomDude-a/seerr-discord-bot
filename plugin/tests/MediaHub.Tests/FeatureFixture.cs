using System.Net;
using System.Net.Http.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MediaHub.Tests;

internal sealed class FeatureFixture : IDisposable
{
    public readonly string Directory = Path.Combine(Path.GetTempPath(), "mediahub-features-" + Guid.NewGuid());
    public readonly User User = new("Viewer", "provider", "reset") { Id = Guid.NewGuid() };
    public readonly Mock<IUserManager> Users = new();
    public readonly Mock<ILibraryManager> Library = new();
    public readonly Mock<IUserDataManager> Data = new();
    public readonly Mock<ISessionManager> Sessions = new();
    public readonly HubStore Store;
    public readonly SettingsService Settings;
    public readonly HostIdentity Identity;
    public readonly SeerrBridge Bridge;
    public readonly MediaActivity Activity;
    public readonly OperatorAuthority Operators;
    public readonly OperatorActor Actor;
    public long Permissions = 32;
    public FeatureFixture()
    {
        Store = new(Directory); Settings = new(Store);
        Settings.Update(new(0, true, "https://jellyfin.example/base", ["555"], "http://seerr", "bot-private-token", "seerr-private-key", Features: new(new("http://lidarr", "lidarr-private-key", "/media", 2, 3, true), new("http://readarr", "readarr-private-key", "/media", 2, 3, true), ["42"])));
        Users.Setup(u => u.GetUserById(User.Id)).Returns(User);
        Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var session = new SessionInfo(Sessions.Object, NullLogger.Instance) { Id = "browser", UserId = User.Id, DeviceId = "device" };
        Sessions.Setup(s => s.GetSessionByAuthenticationToken("token", "device", It.IsAny<string>())).ReturnsAsync(session);
        var code = Store.BeginLink("42"); Store.Approve(code, User.Id, "42", Settings.ProtectLink("token", "42", User.Id, "device"), "device");
        Identity = new(Store, Settings, Users.Object, Sessions.Object);
        Bridge = new(Store, Settings, Identity, () => new SeerrHandler(this));
        Activity = new(Store, Library.Object, Users.Object, Data.Object); Operators = new(Identity, Store, Settings, Bridge); Actor = new(User.Id, "42");
    }
    public void Update(ExtendedUpdate features)
    { var s = Settings.Current; Settings.Update(new(s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null, Features: features)); }
    public ExtendedUpdate Features(string[]? operators = null, bool seerr = false, bool members = false, int limit = 10, int recipients = 100) => new(new("http://lidarr", null, "/media", 2, 3, true, limit), new("http://readarr", null, "/media", 2, 3, true, limit), operators ?? ["42"], SeerrOperators: seerr, MembersIntent: members, MaxRecipients: recipients);
    private sealed class SeerrHandler(FeatureFixture f) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.PathAndQuery; object body;
            if (path.StartsWith("/api/v1/user?")) body = new { pageInfo = new { results = 1 }, results = new[] { new { id = 7, jellyfinUserId = f.User.Id.ToString() } } };
            else if (path == "/api/v1/auth/me") body = new { id = 7, permissions = f.Permissions };
            else if (path == "/api/v1/user/7") body = new { id = 7, jellyfinUserId = f.User.Id.ToString() };
            else throw new InvalidOperationException("Unexpected scope route: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
    public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
}
