using System.Security.Cryptography;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class IsolationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mediahub-isolation-tests-" + Guid.NewGuid());
    private string Data => Path.Combine(root, "jellyfin-data");
    private string PluginData => Path.Combine(Data, "media-hub");
    public IsolationTests()
    {
        Directory.CreateDirectory(Data);
        using (var host = Raw(Path.Combine(Data, "jellyfin.db")))
        using (var command = HubStore.Command(host, "CREATE TABLE JellyfinUsers(id TEXT); INSERT INTO JellyfinUsers VALUES('must-retain')")) command.ExecuteNonQuery();
        File.WriteAllText(Path.Combine(Data, "system.xml"), "<ServerConfiguration><BaseUrl>/keep</BaseUrl></ServerConfiguration>");
        File.WriteAllText(Path.Combine(Data, "network.xml"), "<NetworkConfiguration>unchanged</NetworkConfiguration>");
    }
    private static SqliteConnection Raw(string file)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()); db.Open(); return db;
    }
    private IHost Host(IPluginPageBridge bridge, ILoggerProvider? logger = null)
    {
        var paths = new Mock<IApplicationPaths>(); paths.SetupGet(p => p.DataPath).Returns(Data);
        return new HostBuilder().ConfigureLogging(logging =>
        {
            logging.ClearProviders(); if (logger is not null) logging.AddProvider(logger);
        }).ConfigureServices(services =>
        {
            services.AddSingleton(paths.Object);
            services.AddSingleton(Mock.Of<IUserManager>());
            services.AddSingleton(Mock.Of<ISessionManager>());
            services.AddSingleton(Mock.Of<ISyncPlayManager>());
            services.AddSingleton(Mock.Of<ILibraryManager>());
            services.AddSingleton(Mock.Of<IUserDataManager>());
            new ServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
            services.AddSingleton(bridge);
        }).Build();
    }
    private Dictionary<string, string> Snapshot(bool hostOnly = false) => Directory.EnumerateFiles(Data, "*", SearchOption.AllDirectories)
        .Where(path => !hostOnly || !path.StartsWith(PluginData + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Where(path => path != Path.Combine(PluginData, "hub.db-wal") && path != Path.Combine(PluginData, "hub.db-shm"))
        .ToDictionary(path => Path.GetRelativePath(Data, path), path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        });
    private static void Unchanged(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        Assert.Equal(before.Count, after.Count);
        foreach (var file in before) Assert.Equal(file.Value, after[file.Key]);
    }
    [Fact]
    public async Task InstallStartStopAndRemovalLeaveHostDatabaseAndSettingsByteForByteUntouched()
    {
        var before = Snapshot(); var bridge = new Mock<IPluginPageBridge>(); bridge.Setup(b => b.Register()).Returns(true);
        using (var host = Host(bridge.Object))
        {
            // Registering/installing services does not initialize or mutate any database.
            Assert.False(Directory.Exists(PluginData));
            await host.StartAsync();
            var state = host.Services.GetRequiredService<HubStore>();
            Assert.Equal(Path.GetFullPath(PluginData), state.DirectoryPath);
            using (var db = state.Open())
            using (var marker = HubStore.Command(db, "PRAGMA application_id")) Assert.Equal((long)HubStore.ApplicationId, marker.ExecuteScalar());
            await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Mock.Get(host.Services.GetRequiredService<IUserDataManager>()).VerifyNoOtherCalls();
            Mock.Get(host.Services.GetRequiredService<ILibraryManager>()).VerifyNoOtherCalls();
            Mock.Get(host.Services.GetRequiredService<IUserManager>()).VerifyNoOtherCalls();
            Mock.Get(host.Services.GetRequiredService<ISyncPlayManager>()).VerifyNoOtherCalls();
        }
        bridge.Verify(b => b.Remove(), Times.Once);
        var after = Snapshot(hostOnly: true);
        Unchanged(before, after);
        Assert.True(File.Exists(Path.Combine(PluginData, "hub.db"))); // No automatic uninstall deletion, even of plugin-owned data.
    }
    [Theory]
    [InlineData("corrupt-db")]
    [InlineData("foreign-db")]
    [InlineData("future-schema")]
    [InlineData("invalid-settings")]
    [InlineData("missing-key")]
    [InlineData("state-directory-is-file")]
    public async Task BadPluginStateCannotStopTheHostOrResetFiles(string problem)
    {
        if (problem == "state-directory-is-file") File.WriteAllText(PluginData, "not a directory");
        else
        {
            Directory.CreateDirectory(PluginData);
            switch (problem)
            {
                case "corrupt-db": File.WriteAllText(Path.Combine(PluginData, "hub.db"), "not sqlite"); break;
                case "foreign-db": File.Copy(Path.Combine(Data, "jellyfin.db"), Path.Combine(PluginData, "hub.db")); break;
                case "future-schema":
                    _ = new HubStore(PluginData); SqliteConnection.ClearAllPools();
                    using (var db = Raw(Path.Combine(PluginData, "hub.db")))
                    using (var future = HubStore.Command(db, "PRAGMA user_version=999")) future.ExecuteNonQuery();
                    break;
                default:
                    _ = new HubStore(PluginData); SqliteConnection.ClearAllPools();
                    File.WriteAllText(Path.Combine(PluginData, "settings.json"), "{invalid-json");
                    if (problem != "missing-key") File.WriteAllBytes(Path.Combine(PluginData, "secrets.key"), new byte[32]);
                    break;
            }
        }
        var before = Snapshot(); var warning = new WarningSignal();
        using var host = Host(Mock.Of<IPluginPageBridge>(), warning);
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await warning.Seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        var controller = new HubController(host.Services) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsAssignableFrom<ObjectResult>(await controller.Status());
        Assert.InRange(response.StatusCode!.Value, 400, 599); // Fail closed within the route, not at controller activation.
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Unchanged(before, Snapshot());
    }
    [Fact]
    public async Task FailingOptionalMenuRegistrationAndCleanupDoNotEscapeToTheHost()
    {
        var bridge = new Mock<IPluginPageBridge>(); bridge.Setup(b => b.Register()).Throws<InvalidOperationException>();
        var page = new PageRegistration(bridge.Object, NullLogger<PageRegistration>.Instance);
        await page.StartAsync(CancellationToken.None); Assert.False(page.Available);
        await page.StopAsync(CancellationToken.None); bridge.Verify(b => b.Remove(), Times.Never);
        bridge.Setup(b => b.Register()).Returns(true); bridge.Setup(b => b.Remove()).Throws<InvalidOperationException>();
        await page.StartAsync(CancellationToken.None); Assert.True(page.Available);
        await page.StopAsync(CancellationToken.None); Assert.False(page.Available);
    }
    [Fact]
    public async Task FailingLoggingSinkCannotEscapePluginStartupOrCleanupFailureContainment()
    {
        var logger = new ThrowingLogger<PageRegistration>(); var bridge = new Mock<IPluginPageBridge>();
        bridge.Setup(b => b.Register()).Throws(new IOException("Simulated registration failure"));
        var pages = new PageRegistration(bridge.Object, logger); await pages.StartAsync(default); Assert.False(pages.Available);
        bridge.Setup(b => b.Register()).Returns(true); bridge.Setup(b => b.Remove()).Throws(new IOException("Simulated cleanup failure"));
        await pages.StartAsync(default); Assert.True(pages.Available); await pages.StopAsync(default); Assert.False(pages.Available);
        Assert.Equal(2, logger.Calls);
    }
    [Fact]
    public void ForeignDatabaseAndLinkedStatePathsAreRejectedBeforeWrites()
    {
        Directory.CreateDirectory(PluginData);
        var file = Path.Combine(PluginData, "hub.db"); File.Copy(Path.Combine(Data, "jellyfin.db"), file);
        var before = Snapshot(); Assert.Throws<HubError>(() => new HubStore(PluginData)); Unchanged(before, Snapshot());
        File.Delete(file); // Only this test's own copied fixture, never a host file.
        try { File.CreateSymbolicLink(file, Path.Combine(Data, "jellyfin.db")); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; } // Linux CI exercises this without Windows symlink privilege.
        Assert.Throws<HubError>(() => new HubStore(PluginData));
        Assert.Equal(before["jellyfin.db"], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Data, "jellyfin.db")))));
    }
    [Theory]
    [InlineData("directory")]
    [InlineData("settings.json")]
    [InlineData("secrets.key")]
    [InlineData("hub.db-wal")]
    [InlineData("hub.db-shm")]
    [InlineData("hub.db-journal")]
    public void LinkedDirectoriesSettingsKeysAndSqliteSidecarsCannotRedirectWrites(string name)
    {
        var target = Path.Combine(Data, "system.xml"); var before = File.ReadAllText(target);
        HubStore? store = null;
        try
        {
            if (name == "directory") Directory.CreateSymbolicLink(PluginData, Data);
            else { store = new HubStore(PluginData); File.CreateSymbolicLink(Path.Combine(PluginData, name), target); }
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        if (name is "settings.json" or "secrets.key") Assert.Throws<HubError>(() => new SettingsService(store!));
        else Assert.Throws<HubError>(() => new HubStore(PluginData));
        Assert.Equal(before, File.ReadAllText(target));
    }
    private sealed class WarningSignal : ILoggerProvider, ILogger
    {
        public readonly TaskCompletionSource Seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel >= LogLevel.Warning) Seen.TrySetResult(); }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
