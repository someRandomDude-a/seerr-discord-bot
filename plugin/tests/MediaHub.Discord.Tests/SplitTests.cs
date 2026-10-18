extern alias Integration;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Common.Configuration;
using MediaHub.Packager;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using DiscordPlugin = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.Plugin;
using Dependencies = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.IntegrationDependencies;

namespace MediaHub.Discord.Tests;

public sealed class SplitTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)] [InlineData(true, false, false)] [InlineData(true, true, true)]
    public void IntegrationRequiresBothProviders(bool core, bool parties, bool expected)
    {
        var client = new Mock<IWatchlistDeletionClient>(); client.SetupGet(c => c.IsAvailable).Returns(core);
        var syncplay = new Mock<IWatchPartyService>(); syncplay.SetupGet(p => p.IsAvailable).Returns(parties);
        Assert.Equal(expected, new Dependencies(client.Object, syncplay.Object).Ready);
    }
    [Fact]
    public void AdapterCannotReferenceCoreImplementationOrDatabaseDrivers()
    {
        Assert.DoesNotContain(typeof(DiscordPlugin).Assembly.GetReferencedAssemblies(), name => name.Name is "Jellyfin.Plugin.MediaHub" or "Microsoft.Data.Sqlite" or "SQLitePCLRaw.core");
    }
    [Fact]
    public void ActivityRoutesAssetsAndConfigurationPageBelongToTheAdapterNotTheCore()
    {
        var adapter = typeof(DiscordPlugin).Assembly; var core = typeof(Jellyfin.Plugin.MediaHub.Plugin).Assembly;
        Assert.Null(core.GetType("Jellyfin.Plugin.MediaHub.ActivityController")); Assert.Null(core.GetType("Jellyfin.Plugin.MediaHub.ActivitySessions"));
        Assert.DoesNotContain(core.GetManifestResourceNames(), name => name.EndsWith("activity.bundle.js", StringComparison.Ordinal));
        foreach (var name in new[] { "activity.bundle.js", "activity.css", "hub.html", "hub.js", "messenger.js", "settings.html", "settings.js" })
        { using var resource = adapter.GetManifestResourceStream("Jellyfin.Plugin.MediaHub.Discord.Web." + name); Assert.NotNull(resource); Assert.True(resource.Length > 0); }
    }
    [Fact]
    public void DistinctIdentityAndMetadataDoNotInitializeState()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediahub-discord-metadata-" + Guid.NewGuid());
        var plugin = new DiscordPlugin(Mock.Of<IApplicationPaths>(p => p.DataPath == root));
        Assert.Equal(Guid.Parse("8456d36a-3c89-4b6d-9959-9a74566cc252"), plugin.Id);
        Assert.False(Directory.Exists(root)); Assert.Equal(typeof(DiscordPlugin).Assembly.GetName().Version, plugin.Version);
    }
    [Fact]
    public void DiscordPackageHasItsOwnAllowlistAndCannotBundleCoreOrProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediahub-discord-package-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var built = Path.GetDirectoryName(typeof(DiscordPlugin).Assembly.Location)!; var input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
            foreach (var name in CatalogBuilder.DiscordRuntimeFiles.Where(name => name.EndsWith(".dll", StringComparison.Ordinal))) File.Copy(Path.Combine(built, name), Path.Combine(input, name));
            File.WriteAllText(Path.Combine(input, "Jellyfin.Plugin.MediaHub.Discord.deps.json"), "{}");
            var metadata = Path.Combine(root, "metadata.json");
            File.WriteAllText(metadata, new JsonObject { ["guid"] = "8456d36a-3c89-4b6d-9959-9a74566cc252", ["name"] = "Discord Integration", ["overview"] = "Preview", ["description"] = "Dependent integration", ["category"] = "General", ["targetAbi"] = "12.2.0" }.ToJsonString());
            var result = CatalogBuilder.Create(input, Path.Combine(root, "package"), metadata, "someRandomDude-a/seerr-discord-bot", typeof(DiscordPlugin).Assembly.GetName().Version!.ToString(), plugin: PluginPackage.Discord);
            using var zip = ZipFile.OpenRead(result.Archive);
            Assert.All(zip.Entries, entry => Assert.True(CatalogBuilder.DiscordRuntimeFiles.Contains(entry.FullName) || entry.FullName is "LICENSE" or "THIRD_PARTY_NOTICES.md"));
            Assert.Contains(zip.Entries, entry => entry.FullName == "Jellyfin.Plugin.PrivateSyncPlay.Api.dll");
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName is "Jellyfin.Plugin.MediaHub.dll" or "Jellyfin.Plugin.PrivateSyncPlay.dll");
            Assert.Contains("/discord-v", File.ReadAllText(result.Manifest));
            var previous = Path.Combine(root, "previous.json");
            var coreHistory = new JsonObject { ["guid"] = "c70297bf-77bd-4ad4-93c9-f67598519777", ["versions"] = new JsonArray(new JsonObject {
                ["version"] = "0.0.0.1", ["checksum"] = new string('a', 32), ["targetAbi"] = "12.2.0", ["timestamp"] = "2026-10-01T00:00:00Z",
                ["sourceUrl"] = "https://github.com/someRandomDude-a/seerr-discord-bot/releases/download/plugin-v0.0.0.1/Jellyfin.Plugin.MediaHub_0.0.0.1.zip" }) };
            var retainedHistory = coreHistory.ToJsonString(); File.WriteAllText(previous, new JsonArray(coreHistory).ToJsonString());
            var combined = CatalogBuilder.Create(input, Path.Combine(root, "combined"), metadata, "someRandomDude-a/seerr-discord-bot", typeof(DiscordPlugin).Assembly.GetName().Version!.ToString(), previous, PluginPackage.Discord);
            var catalog = JsonNode.Parse(File.ReadAllText(combined.Manifest))!.AsArray();
            Assert.Equal(2, catalog.Count); Assert.Equal(retainedHistory, catalog[0]!.ToJsonString());
            Assert.Throws<InvalidOperationException>(() => CatalogBuilder.Create(input, Path.Combine(root, "republish"), metadata, "someRandomDude-a/seerr-discord-bot", typeof(DiscordPlugin).Assembly.GetName().Version!.ToString(), combined.Manifest, PluginPackage.Discord));
            File.WriteAllText(Path.Combine(input, "Jellyfin.Plugin.PrivateSyncPlay.Api.dll"), "unreviewed SDK replacement");
            Assert.Throws<InvalidDataException>(() => CatalogBuilder.Create(input, Path.Combine(root, "bad-sdk"), metadata, "someRandomDude-a/seerr-discord-bot", typeof(DiscordPlugin).Assembly.GetName().Version!.ToString(), plugin: PluginPackage.Discord));
        }
        finally { Directory.Delete(root, true); }
    }
}
