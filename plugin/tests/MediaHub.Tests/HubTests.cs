using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class HubTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-tests-" + Guid.NewGuid());
    private readonly HubStore store;
    private readonly SettingsService settings;
    private readonly Mock<IUserManager> users = new();
    private readonly Mock<ISessionManager> sessions = new();
    private readonly Mock<ISyncPlayManager> sync = new();
    private readonly Mock<IPartyMembership> membership = new();
    private readonly HostIdentity identity;
    private readonly PartyService parties;
    private readonly User user = new("Host", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly SessionInfo browser;
    private readonly Guid groupId = Guid.NewGuid();
    public HubTests()
    {
        store = new(directory); settings = new(store);
        browser = new(sessions.Object, NullLogger.Instance) { Id = "real-browser", DeviceId = "browser-device" };
        browser.UserId = user.Id;
        users.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        sessions.SetupGet(manager => manager.Sessions).Returns([browser]);
        sessions.Setup(manager => manager.GetSessionByAuthenticationToken("valid-user-token", browser.DeviceId, It.IsAny<string>())).ReturnsAsync(browser);
        membership.Setup(m => m.CreatorSessionJoined(browser.Id, user.Id, groupId)).Returns(true);
        identity = new(store, settings, users.Object, sessions.Object); parties = new(store, identity, sessions.Object, sync.Object, membership.Object);
        var code = store.BeginLink("42"); store.Approve(code, user.Id, "42", settings.ProtectLink("valid-user-token", "42", user.Id, browser.DeviceId), browser.DeviceId);
        sync.Setup(manager => manager.NewGroup(browser, It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Group());
        sync.Setup(manager => manager.GetGroup(It.IsAny<SessionInfo>(), groupId)).Returns(Group());
    }
    private GroupInfoDto Group(IReadOnlyList<string>? participants = null) => new(groupId, "Party", GroupStateType.Idle, participants ?? [user.Username], DateTime.UtcNow);
    private Party SavedParty() { var party = new Party(HubStore.Nonce(), user.Id, browser.Id, groupId, "Party", HubStore.Now + 3600, "active"); store.AddParty(party); return party; }
    [Fact]
    public async Task SameUsernameWithoutExactCreatorSessionProofCannotAuthorizeInvitations()
    {
        var party = SavedParty(); membership.Setup(m => m.CreatorSessionJoined(browser.Id, user.Id, groupId)).Returns(false);
        await Assert.ThrowsAsync<HubError>(() => parties.Live(party, browser));
        await Assert.ThrowsAsync<HubError>(() => new PartyService(store, identity, sessions.Object, sync.Object).Live(party, browser));
        sync.Verify(s => s.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    private ClaimsPrincipal Principal(bool apiKey = false) => new(new ClaimsIdentity([
        new("Jellyfin-UserId", user.Id.ToString()), new("Jellyfin-DeviceId", browser.DeviceId),
        new("Jellyfin-IsApiKey", apiKey.ToString()), new("Jellyfin-Token", "valid-user-token")], "Jellyfin"));
    [Fact]
    public void SecretsPersistEncryptedAndNeverReadBack()
    {
        settings.Update(new(0, false, "https://jellyfin.example/base", ["555"], "http://seerr", "discord-private", "seerr-private"));
        var disk = File.ReadAllText(Path.Combine(directory, "settings.json"));
        Assert.DoesNotContain("discord-private", disk); Assert.DoesNotContain("seerr-private", disk);
        Assert.DoesNotContain("Encrypted", JsonSerializer.Serialize(settings.Public()));
        Assert.DoesNotContain("discord-private", JsonSerializer.Serialize(settings.Public()));
        Assert.Equal("discord-private", new SettingsService(store).DiscordToken());
    }
    [Fact]
    public void InvitationLinksUseTheOrdinaryUserPluginPagesRouteNotAdminDashboard()
    {
        settings.Update(new(0, false, "https://jellyfin.example/base", ["555"], "", null, null));
        var url = settings.PartyUrl("one-use-ticket");
        Assert.StartsWith("https://jellyfin.example/base/web/index.html#!/userpluginsettings.html?pageUrl=", url);
        Assert.Contains("hubTicket=one-use-ticket", url);
        Assert.Contains("pageUrl=%2Fbase%2Fweb%2FConfigurationPage%3Fname%3Dmediahub", url);
        Assert.DoesNotContain("#!/configurationpage", url);
        Assert.DoesNotContain("valid-user-token", url);
    }
    [Fact]
    public void MissingKeyDoesNotOverwriteRetainedCredentials()
    {
        File.Delete(Path.Combine(directory, "secrets.key"));
        Assert.Throws<HubError>(() => new SettingsService(store));
        Assert.False(File.Exists(Path.Combine(directory, "secrets.key")));
        Assert.Equal(user.Id, store.LinkedUser("42"));
    }
    [Fact]
    public void StaleSettingsCannotOverwriteNewerSave()
    {
        var update = new SettingsUpdate(0, false, "https://jellyfin.example", ["555"], "", null, null);
        settings.Update(update); Assert.Throws<HubError>(() => settings.Update(update));
        Assert.Equal(1, settings.Current.Revision);
    }
    [Theory]
    [InlineData("http://jellyfin.example")]
    [InlineData("https://user:secret@jellyfin.example")]
    [InlineData("https://jellyfin.example/?token=secret")]
    public void UnsafePublicUrlsAreRejected(string url) => Assert.Throws<HubError>(() => settings.Update(new(0, false, url, [], "", null, null)));
    [Fact]
    public void LinkApprovalRequiresCorrectDiscordIdAndCannotRebindIdentity()
    {
        var code = store.BeginLink("7");
        Assert.Throws<HubError>(() => store.Approve(code, Guid.NewGuid(), "wrong", "encrypted", "device"));
        Assert.Throws<HubError>(() => store.Approve(code, user.Id, "7", "encrypted", "device"));
        var same = store.BeginLink("42"); store.Approve(same, user.Id, "42", "renewed-ciphertext", "device");
        Assert.Equal("renewed-ciphertext", store.Credential("42").Token);
        Assert.Throws<HubError>(() => store.Approve(same, user.Id, "42", "encrypted", "device"));
    }
    [Fact]
    public async Task RevokedTokensBlockDiscordAccessWithoutDeletingBinding()
    {
        sessions.Setup(manager => manager.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ThrowsAsync(new InvalidOperationException());
        await Assert.ThrowsAsync<HubError>(() => identity.Discord("42"));
        Assert.Equal(user.Id, store.LinkedUser("42")); Assert.NotEmpty(store.Credential("42").Token);
    }
    [Fact]
    public async Task ApiKeysAndDisabledUsersCannotActAsBrowser()
    {
        await Assert.ThrowsAsync<HubError>(() => identity.Browser(Principal(true)));
        user.SetPermission(PermissionKind.IsDisabled, true);
        await Assert.ThrowsAsync<HubError>(() => identity.Browser(Principal()));
    }
    [Fact]
    public async Task CreatorIsActualBrowserUserNotBotOrAdmin()
    {
        var created = await parties.Create(browser, user, "Watch together", CancellationToken.None);
        Assert.Equal(browser.Id, created.CreatorSession); Assert.Equal(user.Id, created.Creator); Assert.Equal(groupId, created.Group);
        sync.Verify(manager => manager.NewGroup(browser, It.Is<NewGroupRequest>(request => request.GroupName == "Watch together"), CancellationToken.None), Times.Once);
    }
    [Fact]
    public async Task ConcurrentCreateDoesNotCreateDuplicateGroups()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        { try { await parties.Create(browser, user, "Party", CancellationToken.None); return true; } catch (HubError) { return false; } }));
        Assert.Single(results, success => success);
        sync.Verify(manager => manager.NewGroup(It.IsAny<SessionInfo>(), It.IsAny<NewGroupRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task CannotCreateOrJoinWhenSyncPlayPolicyDisallowsIt()
    {
        user.SyncPlayAccess = SyncPlayUserAccessType.None;
        await Assert.ThrowsAsync<HubError>(() => parties.Create(browser, user, "Party", CancellationToken.None));
        var party = SavedParty(); var ticket = store.Ticket(party, user.Id);
        await Assert.ThrowsAsync<HubError>(() => parties.Join(browser, ticket, CancellationToken.None));
        sync.Verify(manager => manager.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public void TicketsAreAccountBoundOneUseAndSurviveRestart()
    {
        var party = SavedParty(); var token = store.Ticket(party, user.Id);
        Assert.Throws<HubError>(() => store.ConsumeTicket(token, Guid.NewGuid()));
        Assert.Equal(party.Id, new HubStore(directory).ConsumeTicket(token, user.Id).Id);
        Assert.Throws<HubError>(() => store.ConsumeTicket(token, user.Id));
    }
    [Fact]
    public async Task ClosedInvitationsAndMissingGroupsNeverJoin()
    {
        var party = SavedParty(); var token = store.Ticket(party, user.Id); store.Close(party.Id);
        await Assert.ThrowsAsync<HubError>(() => parties.Join(browser, token, CancellationToken.None));
        sync.Verify(manager => manager.JoinGroup(It.IsAny<SessionInfo>(), It.IsAny<JoinGroupRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task HostLeavingTheGroupInvalidatesInvitations()
    {
        var party = SavedParty(); sync.Setup(manager => manager.GetGroup(browser, groupId)).Returns(Group([]));
        await Assert.ThrowsAsync<HubError>(() => parties.Live(party, browser));
    }
    [Fact]
    public async Task NativeGroupDisappearanceInvalidatesInvitations()
    {
        var party = SavedParty(); sync.Setup(manager => manager.GetGroup(browser, groupId)).Returns((GroupInfoDto)null!);
        await Assert.ThrowsAsync<HubError>(() => parties.Live(party, browser));
    }
    [Fact]
    public void DmInvitationsCannotBeForwardedOrUsedInAChannel()
    {
        var invite = new Invitation("id", "party", "7", "555", null, HubStore.Now + 60);
        invite.RequireAudience("7", null, "dm", ["555"]);
        Assert.Throws<HubError>(() => invite.RequireAudience("42", null, "dm", ["555"]));
        Assert.Throws<HubError>(() => invite.RequireAudience("7", "555", "channel", ["555"]));
        Assert.Throws<HubError>(() => invite.RequireAudience("7", null, "dm", ["999"]));
    }
    [Fact]
    public void PublicInvitationsAreBoundToOriginalGuildAndChannel()
    {
        var invite = new Invitation("id", "party", null, "555", "channel", HubStore.Now + 60);
        invite.RequireAudience("7", "555", "channel", ["555"]);
        Assert.Throws<HubError>(() => invite.RequireAudience("7", "999", "channel", ["555", "999"]));
        Assert.Throws<HubError>(() => invite.RequireAudience("7", "555", "other-channel", ["555"]));
        Assert.Throws<HubError>(() => invite.RequireAudience("7", null, "dm", ["555"]));
    }
    [Fact]
    public void ExpiredTicketsAndInvitationsCannotBeUsed()
    {
        var party = SavedParty(); var ticket = store.Ticket(party, user.Id);
        using (var db = store.Open())
        using (var command = HubStore.Command(db, "UPDATE tickets SET expires=0")) command.ExecuteNonQuery();
        Assert.Throws<HubError>(() => store.ConsumeTicket(ticket, user.Id));
        Assert.Throws<HubError>(() => new Invitation("id", party.Id, null, "555", "channel", 0).RequireAudience("7", "555", "channel", ["555"]));
    }
    [Fact]
    public async Task AcceptedJoinDoesNotFalselyClaimPlaybackConfirmation()
    {
        var party = SavedParty(); var token = store.Ticket(party, user.Id);
        var result = JsonSerializer.Serialize(await parties.Join(browser, token, CancellationToken.None));
        Assert.Contains("\"Confirmed\":false", result);
        sync.Verify(manager => manager.JoinGroup(browser, It.Is<JoinGroupRequest>(request => request.GroupId == groupId), CancellationToken.None), Times.Once);
    }
    [Fact]
    public void ClosingInvitesDoesNotDestroyGroupOrKickParticipants()
    {
        var party = SavedParty(); parties.Close(browser, party.Id);
        sync.VerifyNoOtherCalls(); Assert.Equal("closed", store.GetParty(party.Id).State);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
