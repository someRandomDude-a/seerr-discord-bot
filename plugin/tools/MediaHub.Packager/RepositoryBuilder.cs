using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MediaHub.Packager;

public sealed record RepositoryResult(PackageResult Core, PackageResult Discord, string Manifest);

public static partial class CatalogBuilder
{
    /// <summary>Combine authoritative published snapshots without dropping another plugin's history.</summary>
    public static JsonArray MergeHistory(IEnumerable<string> manifests)
    {
        var entries = new Dictionary<Guid, JsonObject>(); long bytes = 0; var files = 0;
        foreach (var file in manifests)
        {
            if (++files > 32 || (bytes += new FileInfo(file).Length) > 16 * 1024 * 1024) throw new InvalidDataException("Published history exceeds its merge budget.");
            var snapshot = ReadJson(file) as JsonArray ?? throw new InvalidDataException("Published history must be a catalog array.");
            if (snapshot.Count is 0 or > 1000) throw new InvalidDataException("Published history is empty or oversized; refusing to replace it.");
            var seen = new HashSet<Guid>();
            foreach (var node in snapshot)
            {
                if (node is not JsonObject incoming || !Guid.TryParse(incoming["guid"]?.GetValue<string>(), out var id) || !seen.Add(id) || incoming["versions"] is not JsonArray { Count: > 0 and <= 1000 } versions)
                    throw new InvalidDataException("Published catalog has missing, duplicate or invalid plugin history.");
                if (!entries.TryGetValue(id, out var retained))
                {
                    retained = (JsonObject)incoming.DeepClone(); retained["versions"] = new JsonArray(); entries.Add(id, retained);
                }
                else if (id != Guid.Parse("c70297bf-77bd-4ad4-93c9-f67598519777") && id != Guid.Parse("8456d36a-3c89-4b6d-9959-9a74566cc252"))
                {
                    var first = (JsonObject)retained.DeepClone(); first.Remove("versions"); var next = (JsonObject)incoming.DeepClone(); next.Remove("versions");
                    if (!JsonNode.DeepEquals(first, next)) throw new InvalidDataException("Unrelated plugin metadata conflicts across published catalogs; review it rather than replacing it.");
                }
                var target = retained["versions"]!.AsArray(); var snapshotVersions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var version in versions)
                {
                    if (version is not JsonObject || version["version"]?.GetValue<string>() is not { Length: > 0 and <= 128 } key || !snapshotVersions.Add(key))
                        throw new InvalidDataException("Published version history is incomplete or duplicated.");
                    var existing = target.SingleOrDefault(row => row!["version"]!.GetValue<string>() == key);
                    if (existing is not null && !JsonNode.DeepEquals(existing, version)) throw new InvalidDataException("Published version metadata conflicts; immutable checksums, URLs and history cannot be overwritten.");
                    if (existing is null) target.Add(version.DeepClone());
                    if (target.Count > 1000) throw new InvalidDataException("Merged release history exceeds its safe size.");
                }
                if (entries.Count > 1000) throw new InvalidDataException("Merged plugin history exceeds its safe size.");
            }
        }
        return new JsonArray(entries.Values.Select(entry => (JsonNode)entry).ToArray());
    }

    /// <summary>Two separate ZIPs and one catalog. Validate both before publishing any output files.</summary>
    public static RepositoryResult CreateRepository(string coreInput, string discordInput, string output,
        string coreMetadata, string discordMetadata, string repository, string version, IEnumerable<string>? history = null)
    {
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Use an empty repository output directory; existing files will not be overwritten.");
        var staging = Path.Combine(Path.GetTempPath(), "mediahub-catalog-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(staging)) throw new IOException("Private catalog staging path already exists.");
        Directory.CreateDirectory(staging);
        try
        {
            var inputs = history?.ToArray() ?? [];
            string? previous = null;
            if (inputs.Length > 0)
            {
                previous = Path.Combine(staging, "previous.json");
                WriteNew(previous, MergeHistory(inputs).ToJsonString(new() { WriteIndented = true }) + "\n");
            }
            var core = Create(coreInput, Path.Combine(staging, "core"), coreMetadata, repository, version, previous, PluginPackage.Core);
            var discord = Create(discordInput, Path.Combine(staging, "discord"), discordMetadata, repository, version, core.Manifest, PluginPackage.Discord);
            // Neither plugin's earlier entries may disappear during the second pass.
            var catalog = ReadJson(discord.Manifest).AsArray();
            if (catalog.Count(row => row?["guid"]?.GetValue<string>() == "c70297bf-77bd-4ad4-93c9-f67598519777") != 1 ||
                catalog.Count(row => row?["guid"]?.GetValue<string>() == "8456d36a-3c89-4b6d-9959-9a74566cc252") != 1)
                throw new InvalidDataException("Both separate plugins must appear exactly once in the repository.");
            Directory.CreateDirectory(output);
            if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Repository output changed while building; nothing was overwritten.");
            var coreFile = Path.Combine(output, Path.GetFileName(core.Archive)); var discordFile = Path.Combine(output, Path.GetFileName(discord.Archive));
            File.Copy(core.Archive, coreFile, overwrite: false); File.Copy(discord.Archive, discordFile, overwrite: false);
            var manifest = Path.Combine(output, "manifest.json"); File.Copy(discord.Manifest, manifest, overwrite: false);
            WriteNew(Path.Combine(output, "SHA256SUMS"), $"{core.Sha256}  {Path.GetFileName(coreFile)}\n{discord.Sha256}  {Path.GetFileName(discordFile)}\n");
            var url = $"https://{repository.Split('/')[0].ToLowerInvariant()}.github.io/{repository.Split('/')[1]}/manifest.json";
            var coreUrl = $"https://github.com/{repository}/releases/download/plugin-v{version}/{Path.GetFileName(coreFile)}";
            var discordUrl = $"https://github.com/{repository}/releases/download/discord-v{version}/{Path.GetFileName(discordFile)}";
            WriteNew(Path.Combine(output, "index.html"), $"""
                <!doctype html><html lang="en"><meta charset="utf-8"><title>Jellyfin plugin catalogue</title>
                <h1>Watchlist Deletion and Discord Integration · Preview</h1>
                <p>Two separate plugins for Jellyfin 12.2. Add this repository once, then install each desired plugin from the catalogue:</p>
                <p><a href="manifest.json">{WebUtility.HtmlEncode(url)}</a></p>
                <ul><li><a href="{WebUtility.HtmlEncode(coreUrl)}">Watchlist Deletion {WebUtility.HtmlEncode(version)}</a> — native watchlists, notifications and deletion consent; no Discord required.</li>
                <li><a href="{WebUtility.HtmlEncode(discordUrl)}">Discord Integration {WebUtility.HtmlEncode(version)}</a> — install the core and the separately published Private SyncPlay provider first.</li></ul>
                <p>Install compatible File Transformation and Plugin Pages for native Web controls. Dependencies are not silently bundled or installed.</p>
                <p><a href="SHA256SUMS">SHA-256 checksums</a> · <a href="https://github.com/{WebUtility.HtmlEncode(repository)}/blob/plugin-v{WebUtility.HtmlEncode(version)}/README.md">Installation, migration and preview limits</a></p>
                <p>These are preview builds, not a production-safety certification. Automatic removal defaults off.</p></html>
                """);
            return new(new(coreFile, manifest, core.Sha256), new(discordFile, manifest, discord.Sha256), manifest);
        }
        finally { Directory.Delete(staging, recursive: true); } // Only the new private staging directory created by this invocation.
    }
    private static void WriteNew(string path, string contents)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var bytes = Encoding.UTF8.GetBytes(contents); file.Write(bytes); file.Flush(true);
    }
}
