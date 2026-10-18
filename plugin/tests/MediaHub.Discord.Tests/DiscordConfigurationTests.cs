extern alias Integration;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.MediaHub;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaHub.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Data;
using Moq;
using Xunit;
using Configuration = Integration::Jellyfin.Plugin.MediaHub.DiscordConfiguration;
using Update = Integration::Jellyfin.Plugin.MediaHub.DiscordConfigurationUpdate;
using AdapterError = Integration::Jellyfin.Plugin.MediaHub.HubError;
using AdapterRegistration = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.ServiceRegistrator;

namespace MediaHub.Discord.Tests;

public sealed class DiscordConfigurationTests
{
    private static DiscordConfigurationSnapshot Legacy => new(7, true, "retained-private-bot", ["555"], "123456", "retained-private-oauth", true, true, false, false, ["42"], false, [], 7, 10);
    private static Update Save(int revision = 0, bool import = true) => new(revision, import, true, null, ["555"], "123456", null, true, true, false, false, ["42"], false, [], 7, 10);
    [Fact]
    public void MetadataAndReviewDoNotCreateStateAndFirstSaveRequiresExplicitMigration()
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter"); var configuration = new Configuration(directory, () => Legacy);
        Assert.False(Directory.Exists(directory)); Assert.Null(configuration.Snapshot());
        Assert.True(JsonSerializer.SerializeToElement(configuration.Review()).GetProperty("MigrationReviewRequired").GetBoolean());
        Assert.False(Directory.Exists(directory));
        Assert.Throws<AdapterError>(() => configuration.Update(Save(import: false)));
        Assert.False(File.Exists(Path.Combine(directory, "secrets.key"))); Assert.Null(configuration.Snapshot());
    }
    [Fact]
    public void ExplicitMigrationEncryptsSeparateSettingsAndRetainsOriginalCoreBytes()
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter");
        var originalSettings = File.ReadAllBytes(Path.Combine(f.Directory, "settings.json")); var originalKey = File.ReadAllBytes(Path.Combine(f.Directory, "secrets.key"));
        var configuration = new Configuration(directory, () => Legacy); configuration.Update(Save());
        var saved = configuration.Snapshot()!; Assert.Equal(1, saved.Revision); Assert.Equal(Legacy.BotToken, saved.BotToken);
        var encoded = File.ReadAllText(Path.Combine(directory, "settings.json")); Assert.DoesNotContain(Legacy.BotToken, encoded); Assert.DoesNotContain(Legacy.ClientSecret, encoded);
        var review = JsonSerializer.Serialize(configuration.Review()); Assert.DoesNotContain(Legacy.BotToken, review); Assert.DoesNotContain(Legacy.ClientSecret, review);
        var reopened = new Configuration(directory, () => throw new InvalidOperationException("Legacy must not be read after migration."));
        Assert.Equal(saved.BotToken, reopened.Snapshot()!.BotToken); Assert.Equal(saved.ApplicationId, reopened.Snapshot()!.ApplicationId);
        Assert.Throws<AdapterError>(() => reopened.Update(Save())); Assert.Equal(1, reopened.Snapshot()!.Revision);
        Assert.Equal(originalSettings, File.ReadAllBytes(Path.Combine(f.Directory, "settings.json"))); Assert.Equal(originalKey, File.ReadAllBytes(Path.Combine(f.Directory, "secrets.key")));
    }
    [Theory]
    [InlineData("missing-key")] [InlineData("changed-key")] [InlineData("missing-settings")] [InlineData("changed-settings")]
    public void BrokenAdapterStateIsNeverReplacedByLegacyAndDoesNotDisableNativeCoreState(string failure)
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter"); var configuration = new Configuration(directory, () => Legacy); configuration.Update(Save());
        var coreBytes = File.ReadAllBytes(Path.Combine(f.Directory, "settings.json")); var keyPath = Path.Combine(directory, "secrets.key"); var path = Path.Combine(directory, "settings.json");
        if (failure == "missing-key") File.Delete(keyPath);
        else if (failure == "changed-key") File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        else if (failure == "missing-settings") File.Delete(path);
        else File.WriteAllText(path, "{\"Format\":1,\"Revision\":2,\"Ciphertext\":\"untrusted\"}");
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (_, _) => configuration.SnapshotJson).BuildServiceProvider();
        var settings = new SettingsService(f.Store) { Overlay = new DiscordConfigurationOverlay(services).Apply };
        var current = settings.Current; Assert.False(current.DiscordEnabled); Assert.False(current.Features.ActivityEnabled); Assert.Empty(current.Features.Operators);
        Assert.True(current.AdapterConfigurationUnavailable); Assert.True(current.AdapterConfigurationPresent);
        Assert.NotNull(new PersonalService(f.Store, Mock.Of<IViewerMedia>(), settings).State(f.User));
        Assert.Equal(coreBytes, File.ReadAllBytes(Path.Combine(f.Directory, "settings.json")));
        Assert.ThrowsAny<Exception>(() => new Configuration(directory, () => throw new Exception("No legacy fallback allowed.")).Snapshot());
        if (failure == "missing-key") Assert.False(File.Exists(keyPath));
        if (failure == "missing-settings") Assert.False(File.Exists(path));
    }
    [Fact]
    public void RuntimeRevisionInvalidatesDeliveryPlansAndCorePageCannotOverwriteAdapterSettings()
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter"); var configuration = new Configuration(directory, () => Legacy, f.Settings.Protection.MarkDiscordConfigurationMigrated, f.Settings.Protection.DiscordConfigurationMigrated); configuration.Update(Save());
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (_, _) => configuration.SnapshotJson).BuildServiceProvider();
        var settings = new SettingsService(f.Store) { Overlay = new DiscordConfigurationOverlay(services).Apply };
        Assert.Equal(f.Settings.Current.Revision + 1, settings.Current.RuntimeRevision); Assert.Equal("123456", settings.Current.Features.DiscordApplicationId);
        var runtime = settings.Current.RuntimeRevision; configuration.Update(Save(1) with { MessageContent = true }); Assert.Equal(runtime + 1, settings.Current.RuntimeRevision);
        var s = settings.Retained;
        Assert.Throws<HubError>(() => settings.Update(new(s.Revision, false, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null)));
        settings.Update(new(s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null, DeletionDelayDays: 3));
        Assert.Equal(3, settings.Current.DeletionDelayDays); Assert.Equal(runtime + 2, settings.Current.RuntimeRevision); Assert.True(settings.Current.DiscordEnabled);
        Assert.Equal(2, configuration.Snapshot()!.Revision);
    }
    [Fact]
    public void MigratedCustodySurvivesRestartAndMissingWholeAdapterDirectoryCannotReactivateLegacyCredentials()
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter");
        var configuration = new Configuration(directory, () => Legacy, f.Settings.Protection.MarkDiscordConfigurationMigrated, f.Settings.Protection.DiscordConfigurationMigrated);
        configuration.Update(Save()); Assert.True(f.Settings.Protection.DiscordConfigurationMigrated());
        Directory.Move(directory, directory + "-retained"); // Retain all bytes; simulate an unavailable mount, not a reset.
        var restartedAdapter = new Configuration(directory, () => Legacy, wasMigrated: f.Settings.Protection.DiscordConfigurationMigrated);
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (_, _) => restartedAdapter.SnapshotJson).BuildServiceProvider();
        var restartedCore = new SettingsService(new HubStore(f.Directory)) { Overlay = new DiscordConfigurationOverlay(services).Apply };
        Assert.False(restartedCore.Current.DiscordEnabled); Assert.True(restartedCore.Current.AdapterConfigurationUnavailable);
        Assert.False(Directory.Exists(directory)); Assert.NotNull(new PersonalService(f.Store, Mock.Of<IViewerMedia>(), restartedCore).State(f.User));
        Assert.Throws<AdapterError>(() => restartedAdapter.Review()); Assert.Throws<AdapterError>(() => restartedAdapter.Update(Save())); Assert.False(Directory.Exists(directory));
        Directory.Move(directory + "-retained", directory);
        Assert.True(restartedCore.Current.DiscordEnabled); Assert.Equal(1, restartedAdapter.Snapshot()!.Revision);
    }
    [Fact]
    public void NativeOAuthConfigurationPinSurvivesAdapterSecretRewrappingAcrossRestart()
    {
        using var f = new FeatureFixture(); var configuration = new Configuration(Path.Combine(f.Directory, "adapter"), () => Legacy,
            f.Settings.Protection.MarkDiscordConfigurationMigrated, f.Settings.Protection.DiscordConfigurationMigrated); configuration.Update(Save());
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (_, _) => configuration.SnapshotJson).BuildServiceProvider();
        SettingsService Reopen() => new(f.Store) { Overlay = new DiscordConfigurationOverlay(services).Apply };
        var first = Reopen(); var restarted = Reopen();
        Assert.NotEqual(first.Current.Features.DiscordClientSecretEncrypted, restarted.Current.Features.DiscordClientSecretEncrypted);
        string Pin(SettingsService settings) => new LinkFlow(f.Store, settings, f.Identity, f.Bridge, Mock.Of<IDiscordOAuth>()).Pin();
        var originalPin = Pin(first); Assert.Equal(originalPin, Pin(restarted));
        configuration.Update(Save(1) with { ClientSecret = "changed-private-oauth" }); Assert.NotEqual(originalPin, Pin(first)); Assert.Equal(Pin(first), Pin(restarted));
    }
    [Fact]
    public async Task MigrationDoesNotHoldTheSnapshotLockWhileWaitingForTheCoreWriter()
    {
        using var f = new FeatureFixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = new Configuration(Path.Combine(f.Directory, "adapter"), () => Legacy, () => { entered.TrySetResult(); f.Settings.Protection.MarkDiscordConfigurationMigrated(); });
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string>>(CoreApi.DiscordConfigurationKey, (_, _) => configuration.SnapshotJson).BuildServiceProvider();
        var settings = new SettingsService(f.Store) { Overlay = new DiscordConfigurationOverlay(services).Apply };
        using var db = f.Store.Open(); using var writer = db.BeginTransaction();
        var migration = Task.Run(() => configuration.Update(Save()));
        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = await Task.Run(() => settings.Current).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(snapshot.AdapterConfigurationPresent);
        } finally { writer.Rollback(); await migration.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Equal(1, configuration.Snapshot()!.Revision);
    }
    [Fact]
    public async Task ConfigurationEndpointRejectsApiKeysAndNonAdministratorsAndNeverReturnsSavedSecrets()
    {
        using var f = new FeatureFixture();
        var client = new Mock<IWatchlistDeletionClient>();
        client.Setup(c => c.InvokeAsync<GatewaySettings>("", null, "transport.settings", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewaySettings(1, false, [], "https://jellyfin.example/base", new(false, false, 10, "123456", [])));
        var configuration = new Configuration(Path.Combine(f.Directory, "adapter"), () => Legacy);
        using var services = new ServiceCollection().AddSingleton(f.Users.Object).AddSingleton(f.Sessions.Object).AddSingleton(client.Object)
            .AddSingleton<Integration::Jellyfin.Plugin.MediaHub.Rpc>().AddSingleton<Integration::Jellyfin.Plugin.MediaHub.HostIdentity>()
            .AddSingleton<Integration::Jellyfin.Plugin.MediaHub.SettingsService>().AddSingleton(configuration).BuildServiceProvider();
        Integration::Jellyfin.Plugin.MediaHub.DiscordConfigurationController Controller(bool apiKey) => new(services) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-IsApiKey", apiKey.ToString()), new("Jellyfin-UserId", f.User.Id.ToString()), new("Jellyfin-Token", "token"), new("Jellyfin-DeviceId", "device")], "Fixture")) } } };
        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await Controller(true).Read()).StatusCode);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(await Controller(false).Read()).StatusCode);
        f.User.SetPermission(PermissionKind.IsAdministrator, true);
        var response = Assert.IsType<OkObjectResult>(await Controller(false).Read()); var json = JsonSerializer.Serialize(response.Value);
        Assert.DoesNotContain(Legacy.BotToken, json); Assert.DoesNotContain(Legacy.ClientSecret, json); Assert.Contains("HasBotToken", json);
        f.User.SetPermission(PermissionKind.IsDisabled, true); Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(await Controller(false).Read()).StatusCode);
        Assert.Null(configuration.Snapshot()); // Opening/rejecting configuration never migrates or activates it.
    }
    [Fact]
    public void DuplicateLegacyAllowlistsCanBeReviewedWithoutRewritingTheirOriginalMeaningOrState()
    {
        using var f = new FeatureFixture(); var legacy = Legacy with { AllowedGuilds = ["555", "555"], Operators = ["42", "42"] };
        var configuration = new Configuration(Path.Combine(f.Directory, "adapter"), () => legacy);
        var review = JsonSerializer.SerializeToElement(configuration.Review()); Assert.Single(review.GetProperty("AllowedGuilds").EnumerateArray());
        configuration.Update(Save()); Assert.Single(configuration.Snapshot()!.AllowedGuilds); Assert.Equal(2, legacy.AllowedGuilds.Length);
    }
    [Fact]
    public async Task CorruptAdapterConfigurationCannotStopTheHostOrReactivateTheLegacyGateway()
    {
        using var f = new FeatureFixture(); var directory = Path.Combine(f.Directory, "adapter"); var configuration = new Configuration(directory, () => Legacy, f.Settings.Protection.MarkDiscordConfigurationMigrated);
        configuration.Update(Save()); File.WriteAllText(Path.Combine(directory, "settings.json"), "corrupt retained configuration");
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders(); var applicationHost = Mock.Of<IServerApplicationHost>();
        new ServiceRegistrator().RegisterServices(builder.Services, applicationHost); new AdapterRegistration().RegisterServices(builder.Services, applicationHost);
        builder.Services.AddSingleton(f.Store); builder.Services.AddSingleton(configuration); builder.Services.AddSingleton(f.Users.Object); builder.Services.AddSingleton(f.Sessions.Object);
        builder.Services.AddSingleton<SettingsService>(provider => new(f.Store) { Overlay = new DiscordConfigurationOverlay(provider).Apply });
        builder.Services.AddSingleton(Mock.Of<IWatchPartyService>(p => p.IsAvailable));
        using var host = builder.Build(); Assert.False(host.Services.GetRequiredService<SettingsService>().Current.DiscordEnabled);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("corrupt retained configuration", File.ReadAllText(Path.Combine(directory, "settings.json")));
        Assert.True(f.Settings.Protection.DiscordConfigurationMigrated());
    }
    [Fact]
    public void FailingLogSinkDoesNotEscapeTheAdapterFailureBoundary()
    {
        var logger = new ThrowingLogger<Integration::Jellyfin.Plugin.MediaHub.DiscordHost>();
        Integration::Jellyfin.Plugin.MediaHub.PluginLog.SafeWarning(logger, "Fixed adapter diagnostic"); Assert.Equal(1, logger.Calls);
    }
    [Fact]
    public async Task AdapterWithMissingCoreStartsAndStopsTheHostWithoutCreatingOrResettingItsState()
    {
        using var f = new FeatureFixture(); var path = Path.Combine(f.Directory, "unused-adapter");
        var builder = Host.CreateApplicationBuilder(); builder.Logging.ClearProviders();
        new AdapterRegistration().RegisterServices(builder.Services, Mock.Of<IServerApplicationHost>());
        builder.Services.AddSingleton(Mock.Of<IApplicationPaths>(p => p.DataPath == path));
        using var host = builder.Build(); await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(Directory.Exists(path));
    }
}
