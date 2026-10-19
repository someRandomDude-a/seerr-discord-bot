extern alias Integration;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MediaHub.Packager;
using Xunit;

namespace MediaHub.Discord.Tests;

public sealed class CatalogueTests : IDisposable
{
    private const string Repository = "example/plugins";
    private const string CoreId = "c70297bf-77bd-4ad4-93c9-f67598519777", DiscordId = "8456d36a-3c89-4b6d-9959-9a74566cc252";
    private readonly string root = Path.Combine(Path.GetTempPath(), "mediahub-catalogue-tests-" + Guid.NewGuid());
    private readonly string core, discord, coreMetadata, discordMetadata;
    private readonly string version = typeof(Jellyfin.Plugin.MediaHub.Plugin).Assembly.GetName().Version!.ToString();
    public CatalogueTests()
    {
        core = Path.Combine(root, "core"); discord = Path.Combine(root, "discord"); Directory.CreateDirectory(core); Directory.CreateDirectory(discord);
        Copy(typeof(Jellyfin.Plugin.MediaHub.Plugin).Assembly.Location, core, CatalogBuilder.RuntimeFiles, "Jellyfin.Plugin.MediaHub.deps.json");
        Copy(typeof(Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.Plugin).Assembly.Location, discord, CatalogBuilder.DiscordRuntimeFiles, "Jellyfin.Plugin.MediaHub.Discord.deps.json");
        coreMetadata = Metadata("core.json", CoreId, "Watchlist Deletion"); discordMetadata = Metadata("discord.json", DiscordId, "Discord Integration");
    }
    private static void Copy(string assembly, string target, IEnumerable<string> names, string dependencies)
    {
        foreach (var name in names.Where(n => n.EndsWith(".dll", StringComparison.Ordinal))) File.Copy(Path.Combine(Path.GetDirectoryName(assembly)!, name), Path.Combine(target, name));
        File.WriteAllText(Path.Combine(target, dependencies), "{}");
    }
    private string Metadata(string file, string id, string name)
    {
        var path = Path.Combine(root, file); File.WriteAllText(path, new JsonObject { ["guid"] = id, ["name"] = name, ["overview"] = "Preview", ["description"] = "Preview", ["category"] = "General", ["targetAbi"] = "12.2.0" }.ToJsonString()); return path;
    }
    private RepositoryResult Build(string output = "repository", params string[] history) => CatalogBuilder.CreateRepository(core, discord, Path.Combine(root, output), coreMetadata, discordMetadata, Repository, version, history);
    private static JsonObject Prior(string id, string releaseVersion)
    {
        var adapter = id == DiscordId; var assembly = adapter ? "Jellyfin.Plugin.MediaHub.Discord" : "Jellyfin.Plugin.MediaHub"; var prefix = adapter ? "discord-v" : "plugin-v";
        return new JsonObject { ["guid"] = id, ["name"] = "Retained name", ["versions"] = new JsonArray(new JsonObject { ["version"] = releaseVersion,
            ["checksum"] = new string('a', 32), ["targetAbi"] = "12.2.0", ["timestamp"] = "2026-10-01T00:00:00Z",
            ["sourceUrl"] = $"https://github.com/{Repository}/releases/download/{prefix}{releaseVersion}/{assembly}_{releaseVersion}.zip", ["changelog"] = "Retained history" }) };
    }
    private string History(string file, params JsonNode[] entries) { var path = Path.Combine(root, file); File.WriteAllText(path, new JsonArray(entries).ToJsonString()); return path; }
    [Fact]
    public void OneCatalogueContainsTwoSeparateInstallablePluginsAndBothRealChecksums()
    {
        var result = Build(); var catalog = JsonNode.Parse(File.ReadAllText(result.Manifest))!.AsArray(); Assert.Equal(2, catalog.Count);
        var checksum = File.ReadAllLines(Path.Combine(root, "repository", "SHA256SUMS")); Assert.Equal(2, checksum.Length);
        foreach (var package in new[] { result.Core, result.Discord })
        {
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(package.Archive))).ToLowerInvariant(), package.Sha256);
            Assert.Contains(package.Sha256 + "  " + Path.GetFileName(package.Archive), checksum);
        }
        var coreEntry = catalog.Single(row => row!["guid"]!.GetValue<string>() == CoreId)!; var discordEntry = catalog.Single(row => row!["guid"]!.GetValue<string>() == DiscordId)!;
        Assert.Contains("/plugin-v" + version + "/", coreEntry["versions"]![0]!["sourceUrl"]!.GetValue<string>());
        Assert.Contains("/discord-v" + version + "/", discordEntry["versions"]![0]!["sourceUrl"]!.GetValue<string>());
        using var coreZip = ZipFile.OpenRead(result.Core.Archive); using var discordZip = ZipFile.OpenRead(result.Discord.Archive);
        Assert.DoesNotContain(coreZip.Entries, e => e.FullName.Contains("Discord", StringComparison.Ordinal));
        Assert.DoesNotContain(discordZip.Entries, e => e.FullName == "Jellyfin.Plugin.MediaHub.dll");
        var index = File.ReadAllText(Path.Combine(root, "repository", "index.html")); Assert.Contains("Watchlist Deletion", index); Assert.Contains("Discord Integration", index);
    }
    [Fact]
    public void DifferentPublishedSnapshotsMergeWithoutLosingEitherPluginsEarlierVersions()
    {
        var oldCore = Prior(CoreId, "0.0.0.1"); var first = History("first.json", oldCore.DeepClone());
        var second = History("second.json", oldCore.DeepClone(), Prior(DiscordId, "0.0.0.1"));
        var result = Build("merged", first, second); var catalog = JsonNode.Parse(File.ReadAllText(result.Manifest))!.AsArray();
        Assert.All(catalog, plugin => Assert.Equal(2, plugin!["versions"]!.AsArray().Count));
        Assert.True(JsonNode.DeepEquals(oldCore["versions"]![0], catalog.Single(row => row!["guid"]!.GetValue<string>() == CoreId)!["versions"]![1]));
        Assert.Throws<InvalidOperationException>(() => Build("republish", result.Manifest));
    }
    [Fact]
    public void ConflictingImmutableHistoryIsRejectedBeforeAnyRepositoryOutput()
    {
        var prior = Prior(CoreId, "0.0.0.1"); var first = History("first.json", prior.DeepClone()); prior["versions"]![0]!["checksum"] = new string('b', 32);
        var second = History("second.json", prior);
        Assert.Throws<InvalidDataException>(() => Build("rejected", first, second)); Assert.False(Directory.Exists(Path.Combine(root, "rejected")));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"guid\":\"c70297bf-77bd-4ad4-93c9-f67598519777\",\"versions\":[]}]")]
    public void EmptyOrMissingPublishedHistoryCannotBeUsedAsAFreshRepository(string input)
    {
        var file = Path.Combine(root, "bad.json"); File.WriteAllText(file, input); Assert.Throws<InvalidDataException>(() => Build("bad-output", file));
    }
    [Fact]
    public void BrokenAdapterDoesNotLeaveAPublishableCoreOnlyCatalogue()
    {
        File.WriteAllText(Path.Combine(discord, "Jellyfin.Plugin.PrivateSyncPlay.Api.dll"), "unreviewed bytes");
        Assert.Throws<InvalidDataException>(() => Build()); Assert.False(Directory.Exists(Path.Combine(root, "repository")));
    }
    [Fact]
    public void ExistingOutputIsNeverOverwrittenOrRemoved()
    {
        var output = Path.Combine(root, "repository"); Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "retained.txt"), "user-owned");
        Assert.Throws<IOException>(() => Build()); Assert.Equal("user-owned", File.ReadAllText(Path.Combine(output, "retained.txt")));
    }
    public void Dispose() => Directory.Delete(root, recursive: true);
}
