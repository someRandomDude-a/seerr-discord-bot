using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaHub;
using MediaHub.Packager;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class PackagingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mediahub-package-tests-" + Guid.NewGuid());
    private readonly string input;
    private readonly string metadata;
    private readonly string version = typeof(Plugin).Assembly.GetName().Version!.ToString();
    private Plugin Instance => new(Mock.Of<IApplicationPaths>(p => p.DataPath == root));
    public PackagingTests()
    {
        input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        var built = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        foreach (var name in CatalogBuilder.RuntimeFiles.Where(name => name.EndsWith(".dll", StringComparison.Ordinal))) File.Copy(Path.Combine(built, name), Path.Combine(input, name));
        File.WriteAllText(Path.Combine(input, "Jellyfin.Plugin.MediaHub.deps.json"), "{}");
        metadata = Path.Combine(root, "plugin.json");
        File.WriteAllText(metadata, new JsonObject { ["guid"] = Instance.Id.ToString(), ["name"] = "Media Hub (Preview)",
            ["overview"] = "Preview", ["description"] = "Not feature complete", ["category"] = "General",
            ["targetAbi"] = typeof(Plugin).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "JellyfinVersion").Value }.ToJsonString());
    }
    private PackageResult Build(string output = "output", string? previous = null) =>
        CatalogBuilder.Create(input, Path.Combine(root, output), metadata, "someRandomDude-a/seerr-discord-bot", version, previous);
    [Fact]
    public void HostCanReadPluginMetadataBeforeAnyStateOrSettingsAreInitialized()
    {
        var instance = Instance; var info = instance.GetPluginInfo();
        Assert.Equal(instance.Id, info.Id); Assert.Equal(typeof(Plugin).Assembly.GetName().Version, instance.Version);
        Assert.Equal(typeof(Plugin).Assembly.Location, instance.AssemblyFilePath);
        Assert.False(Directory.Exists(instance.DataFolderPath));
        instance.OnUninstalling(); Assert.False(Directory.Exists(instance.DataFolderPath));
    }
    [Fact]
    public void IntegratedSetupTutorialShipsInsideThePluginWithoutAnExternalGuideDependency()
    {
        using var html = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaHub.Web.hub.html")!);
        using var script = new StreamReader(typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaHub.Web.hub.js")!);
        var page = html.ReadToEnd(); var code = script.ReadToEnd();
        Assert.Contains("data-tutorial-restart", page); Assert.Contains("data-tutorial-slot=\"config\"", page);
        Assert.Contains("data-tutorial-slot=\"activity\"", page); Assert.Contains("Finish tutorial", page);
        Assert.Contains("function setupTutorial()", code); Assert.Contains("value.Revision === 0", code);
        Assert.Contains("The plugin runs inside Jellyfin", page);
    }
    [Fact]
    public void MaliciousRepositoryJsonAndNewlineVersionsAreRejectedBeforePackaging()
    {
        var text = File.ReadAllText(metadata); File.WriteAllText(metadata, text.Replace("\"name\":", "\"name\":\"ambiguous\",\"name\":"));
        Assert.Throws<InvalidDataException>(() => Build()); File.WriteAllText(metadata, text);
        Assert.Throws<ArgumentException>(() => CatalogBuilder.Create(input, Path.Combine(root, "newline"), metadata, "someRandomDude-a/seerr-discord-bot", version + "\n"));
    }
    [Fact]
    public void PackageContainsOnlyAllowedFilesAndRealChecksums()
    {
        foreach (var name in new[] { "MediaBrowser.Controller.dll", "Microsoft.Data.Sqlite.dll", "settings.json", "secrets.key", "unit-tests.dll" }) File.WriteAllText(Path.Combine(input, name), "must not ship");
        var result = Build(); using var zip = ZipFile.OpenRead(result.Archive);
        Assert.Equal(CatalogBuilder.RuntimeFiles.Length + 2, zip.Entries.Count);
        Assert.All(zip.Entries, entry => Assert.True(CatalogBuilder.RuntimeFiles.Contains(entry.FullName) || entry.FullName is "LICENSE" or "THIRD_PARTY_NOTICES.md"));
        var bytes = File.ReadAllBytes(result.Archive);
        var entry = JsonNode.Parse(File.ReadAllText(result.Manifest))![0]!;
        Assert.Equal(Instance.Id.ToString(), entry["guid"]!.GetValue<string>());
        var release = entry["versions"]![0]!;
        Assert.Equal(version, release["version"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant(), release["checksum"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
        Assert.Contains("/releases/download/plugin-v" + version + "/", release["sourceUrl"]!.GetValue<string>());
    }
    [Fact]
    public void ManifestPreservesEarlierVersionsAndPublishedVersionsAreImmutable()
    {
        var previous = Path.Combine(root, "previous.json");
        File.WriteAllText(previous, new JsonArray(new JsonObject { ["guid"] = Instance.Id.ToString(),
            ["versions"] = new JsonArray(new JsonObject { ["version"] = "0.0.0.1", ["checksum"] = new string('a', 32),
                ["sourceUrl"] = "https://github.com/someRandomDude-a/seerr-discord-bot/releases/download/plugin-v0.0.0.1/Jellyfin.Plugin.MediaHub_0.0.0.1.zip",
                ["targetAbi"] = "12.0.0", ["timestamp"] = "2026-10-01T00:00:00Z" }) }).ToJsonString());
        var result = Build(previous: previous);
        var versions = JsonNode.Parse(File.ReadAllText(result.Manifest))![0]!["versions"]!.AsArray();
        Assert.Equal(2, versions.Count); Assert.Equal("0.0.0.1", versions[1]!["version"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => Build("republish", result.Manifest));
    }
    [Fact]
    public void MissingDependencyCannotProduceAnInstallableManifest()
    {
        File.Delete(Path.Combine(input, "Discord.Net.Rest.dll"));
        Assert.Throws<FileNotFoundException>(() => Build());
        Assert.False(File.Exists(Path.Combine(root, "output", "manifest.json")));
    }
    [Fact]
    public void AssemblyVersionMustMatchCatalogVersion()
    {
        Assert.Throws<InvalidDataException>(() => CatalogBuilder.Create(input, Path.Combine(root, "output"), metadata, "owner/repo", "99.0.0.0"));
    }
    [Fact]
    public void CorruptedHistoryIsNotReplacedWithAnEmptyCatalog()
    {
        var previous = Path.Combine(root, "broken.json"); File.WriteAllText(previous, "{\"not\":\"a catalog\"}");
        Assert.ThrowsAny<Exception>(() => Build(previous: previous));
        Assert.False(File.Exists(Path.Combine(root, "output", "manifest.json")));
    }
    [Theory]
    [InlineData("guid", "00000000-0000-0000-0000-000000000000")]
    [InlineData("targetAbi", "12.99.0")]
    public void ManifestIdentityAndAbiMustMatchTheBuiltAssembly(string field, string value)
    {
        var changed = JsonNode.Parse(File.ReadAllText(metadata))!; changed[field] = value;
        File.WriteAllText(metadata, changed.ToJsonString());
        Assert.Throws<InvalidDataException>(() => Build());
        Assert.False(File.Exists(Path.Combine(root, "output", "manifest.json")));
    }
    [Fact]
    public void ZipBytesAreDeterministicForIdenticalInputs()
    {
        var first = Build(); var second = Build("second");
        Assert.Equal(first.Sha256, second.Sha256);
    }
    [Fact]
    public void ReleasesCannotMoveRepositoryHistoryBackwards()
    {
        var previous = Path.Combine(root, "future.json");
        File.WriteAllText(previous, new JsonArray(new JsonObject { ["guid"] = Instance.Id.ToString(),
            ["versions"] = new JsonArray(new JsonObject { ["version"] = "99.0.0.0" }) }).ToJsonString());
        Assert.Throws<InvalidOperationException>(() => Build(previous: previous));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"guid\":\"c70297bf-77bd-4ad4-93c9-f67598519777\",\"versions\":[]}]")]
    [InlineData("[{\"guid\":\"c70297bf-77bd-4ad4-93c9-f67598519777\",\"versions\":[{\"version\":\"0.0.0.1\",\"checksum\":\"broken\"}]}]")]
    public void MissingOrIncompleteHistoryFailsClosed(string json)
    {
        var previous = Path.Combine(root, "incomplete.json"); File.WriteAllText(previous, json);
        Assert.Throws<InvalidDataException>(() => Build(previous: previous));
        Assert.False(File.Exists(Path.Combine(root, "output", "manifest.json")));
    }
    public void Dispose() => Directory.Delete(root, true);
}
