using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly HostIdentity identity;
    private readonly User user = new("Host", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly SessionInfo browser;
    public HubTests()
    {
        store = new(directory); settings = new(store);
        browser = new(sessions.Object, NullLogger.Instance) { Id = "real-browser", DeviceId = "browser-device" };
        browser.UserId = user.Id;
        users.Setup(manager => manager.GetUserById(user.Id)).Returns(user);
        sessions.SetupGet(manager => manager.Sessions).Returns([browser]);
        sessions.Setup(manager => manager.GetSessionByAuthenticationToken("valid-user-token", browser.DeviceId, It.IsAny<string>())).ReturnsAsync(browser);
        identity = new(store, settings, users.Object, sessions.Object);
        var code = store.BeginLink("42"); store.Approve(code, user.Id, "42", settings.ProtectLink("valid-user-token", "42", user.Id, browser.DeviceId), browser.DeviceId);
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
    public void NativeLinksUseTheOrdinaryUserPluginPagesRouteWithoutRetiredPartyTickets()
    {
        settings.Update(new(0, false, "https://jellyfin.example/base", ["555"], "", null, null));
        var url = settings.WebUrl();
        Assert.StartsWith("https://jellyfin.example/base/web/index.html#/userpluginsettings.html?pageUrl=", url);
        Assert.DoesNotContain("hubTicket", url);
        Assert.Contains("pageUrl=%2Fweb%2FConfigurationPage%3Fname%3Dmediahub", url);
        Assert.DoesNotContain("pageUrl=%2Fbase", url);
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
    private ServiceProvider WatchPartyServices() => new ServiceCollection()
        .AddSingleton(identity).AddSingleton(store).AddSingleton(settings).AddSingleton(sessions.Object)
        .AddPrivateSyncPlayClient().AddSingleton<WatchPartyIntegration>().AddSingleton<WebIntegration>().BuildServiceProvider();
    private HubController Controller(IServiceProvider services, bool apiKey = false) => new(services)
    {
        ControllerContext = new() { HttpContext = new DefaultHttpContext { User = Principal(apiKey) } }
    };
    [Fact]
    public async Task StatusRequiresNoSyncPlayOrCompanionServicesAndDoesNotAdvertiseActiveParties()
    {
        using var services = WatchPartyServices();
        var status = Assert.IsType<OkObjectResult>(await Controller(services).Status());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status.Value));
        Assert.True(json.RootElement.GetProperty("Linked").GetBoolean());
        Assert.True(json.RootElement.GetProperty("Web").GetProperty("Required").GetBoolean());
        Assert.False(json.RootElement.GetProperty("Web").GetProperty("RegistrationsReady").GetBoolean());
        Assert.False(json.RootElement.GetProperty("WatchParty").GetProperty("Available").GetBoolean());
        Assert.False(json.RootElement.GetProperty("Party").GetProperty("Active").GetBoolean());
        Assert.Equal(WatchPartyIntegration.UnavailableCode, json.RootElement.GetProperty("WatchParty").GetProperty("Code").GetString());
    }
    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("close")]
    public async Task RetiredRoutesRejectWithoutChangingRetainedStateAcrossRestart(string action)
    {
        using (var db = store.Open())
        using (var seed = HubStore.Command(db, """
            INSERT INTO parties VALUES('legacy-party','untrusted-creator','old-session','old-group','retained title',9223372036854775807,'active');
            INSERT INTO invitations VALUES('legacy-invite','legacy-party','42','555',NULL,9223372036854775807);
            INSERT INTO tickets VALUES('legacy-ticket','legacy-party','untrusted-user',9223372036854775807,0);
            """)) seed.ExecuteNonQuery();
        string Snapshot(HubStore state)
        {
            using var db = state.Open();
            return string.Join("\n", new[] { "parties", "invitations", "tickets" }.Select(table =>
            {
                using var query = HubStore.Command(db, "SELECT * FROM " + table);
                using var reader = query.ExecuteReader(); var rows = new List<string>();
                while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(JsonSerializer.Serialize(values)); }
                return string.Join("\n", rows);
            }));
        }
        var before = Snapshot(store);
        using var services = WatchPartyServices(); var controller = Controller(services);
        var result = action switch
        {
            "create" => await controller.Create(new("must not create"), CancellationToken.None),
            "join" => await controller.Join(new("legacy-ticket"), CancellationToken.None),
            _ => await controller.Close("legacy-party")
        };
        var rejected = Assert.IsType<ObjectResult>(result); Assert.Equal(503, rejected.StatusCode);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rejected.Value));
        Assert.Equal(WatchPartyIntegration.UnavailableCode, json.RootElement.GetProperty("Code").GetString());
        Assert.Equal(before, Snapshot(new HubStore(directory)));
        Assert.Equal(user.Id, store.LinkedUser("42"));
    }
    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("close")]
    public async Task RetiredRoutesStillRequireAnAuthenticatedNativeUserSession(string action)
    {
        using var services = WatchPartyServices(); var controller = Controller(services, apiKey: true);
        var result = action switch
        {
            "create" => await controller.Create(new("party"), CancellationToken.None),
            "join" => await controller.Join(new("ticket"), CancellationToken.None),
            _ => await controller.Close("party")
        };
        Assert.IsType<BadRequestObjectResult>(result);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
