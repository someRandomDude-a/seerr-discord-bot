using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MediaHub.Packager;

public sealed record PackageResult(string Archive, string Manifest, string Sha256);

public static partial class CatalogBuilder
{
    // Deliberately not a wildcard: never ship Jellyfin/ASP.NET/SQLite host DLLs,
    // developer secrets, test assemblies, PDBs or machine-specific native assets.
    public static readonly string[] RuntimeFiles =
    [
        "Jellyfin.Plugin.MediaHub.dll", "Jellyfin.Plugin.MediaHub.deps.json",
        "Discord.Net.Core.dll", "Discord.Net.Dave.dll", "Discord.Net.Rest.dll", "Discord.Net.WebSocket.dll", "Newtonsoft.Json.dll",
        "Jellyfin.Plugin.PrivateSyncPlay.Api.dll"
    ];
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex VersionPattern();

    private static JsonNode ReadJson(string path)
    {
        const int maximum = 4 * 1024 * 1024;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new InvalidDataException("Repository JSON exceeds its size budget.");
        using var bounded = new MemoryStream(); var buffer = new byte[8192]; int size;
        while ((size = input.Read(buffer)) != 0) { if (bounded.Length + size > maximum) throw new InvalidDataException("Repository JSON changed or exceeds its size budget."); bounded.Write(buffer, 0, size); }
        var bytes = bounded.ToArray();
        using var document = System.Text.Json.JsonDocument.Parse(bytes, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
        var work = new Stack<System.Text.Json.JsonElement>(); work.Push(document.RootElement); var count = 0;
        while (work.TryPop(out var node))
        {
            if (++count > 100000) throw new InvalidDataException("Repository JSON is too complex.");
            if (node.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in node.EnumerateObject()) { if (property.Name.Length > 512 || !names.Add(property.Name)) throw new InvalidDataException("Ambiguous repository JSON fields."); work.Push(property.Value); }
            }
            else if (node.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var child in node.EnumerateArray()) work.Push(child);
            else if (node.ValueKind == System.Text.Json.JsonValueKind.String && node.GetString()!.Length > 65536) throw new InvalidDataException("Oversized repository JSON value.");
        }
        return JsonNode.Parse(bytes, documentOptions: new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 }) ?? throw new InvalidDataException("Repository JSON cannot be null.");
    }

    private static Dictionary<string, string> AssemblyMetadata(string file)
    {
        using var stream = File.OpenRead(file); using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader(); var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw new InvalidDataException("Invalid assembly metadata.");
            var key = blob.ReadSerializedString(); var value = blob.ReadSerializedString();
            if (key is not null && value is not null) values.Add(key, value);
        }
        return values;
    }

    public static PackageResult Create(string input, string output, string metadataPath, string repository,
        string version, string? previousManifest = null)
    {
        if (!RepositoryPattern().IsMatch(repository)) throw new ArgumentException("Repository must be owner/name.");
        if (!VersionPattern().IsMatch(version) || !Version.TryParse(version, out var parsed) ||
            new[] { parsed.Major, parsed.Minor, parsed.Build, parsed.Revision }.Any(part => part > 65534))
            throw new ArgumentException("Use a four-part assembly version, with components 0–65534.");
        var metadata = ReadJson(metadataPath).AsObject();
        if (!Guid.TryParse(metadata["guid"]?.GetValue<string>(), out var guid)) throw new InvalidDataException("Invalid plugin GUID.");
        var targetAbi = metadata["targetAbi"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(targetAbi, out var abi) || abi != new Version(12, 2, 0)) throw new InvalidDataException("Only the latest reviewed Jellyfin 12.2.0 target ABI can be packaged.");
        foreach (var field in new[] { "name", "overview", "description", "category" })
            if (string.IsNullOrWhiteSpace(metadata[field]?.GetValue<string>())) throw new InvalidDataException("Missing metadata: " + field);
        foreach (var name in RuntimeFiles)
            if (!File.Exists(Path.Combine(input, name))) throw new FileNotFoundException("Required release dependency missing: " + name);
        using (var sdk = File.OpenRead(Path.Combine(input, "Jellyfin.Plugin.PrivateSyncPlay.Api.dll")))
            if (Convert.ToHexString(SHA256.HashData(sdk)).ToLowerInvariant() != "0cf21a33c9d44fbb294503e52ad61479501811c2447d3e5dd925b49dd62ca136")
                throw new InvalidDataException("Private SyncPlay public SDK differs from the reviewed 1.1.0 release.");
        if (AssemblyName.GetAssemblyName(Path.Combine(input, RuntimeFiles[0])).Version != parsed)
            throw new InvalidDataException("Manifest version must match the actual plugin assembly version.");
        // Read PE metadata without loading the plugin or its privileged host dependencies.
        var assemblyMetadata = AssemblyMetadata(Path.Combine(input, RuntimeFiles[0]));
        if (!Guid.TryParse(assemblyMetadata.GetValueOrDefault("PluginGuid"), out var assemblyGuid) || assemblyGuid != guid)
            throw new InvalidDataException("Manifest GUID must match the plugin assembly.");
        if (!Version.TryParse(assemblyMetadata.GetValueOrDefault("JellyfinVersion"), out var builtAbi) || builtAbi != abi)
            throw new InvalidDataException("Manifest target ABI must match the Jellyfin API used to build the plugin.");
        var dependencies = ReadJson(Path.Combine(input, RuntimeFiles[1])).AsObject();
        if (dependencies is null) throw new InvalidDataException("Invalid dependency manifest.");

        JsonArray catalog = previousManifest is null ? [] : ReadJson(previousManifest).AsArray();
        if (catalog.Count > 1000 || catalog.Any(row => row is not JsonObject || !Guid.TryParse(row["guid"]?.GetValue<string>(), out _) || row["versions"] is not JsonArray))
            throw new InvalidDataException("Invalid previous repository manifest. Refusing to replace history.");
        var matches = catalog.OfType<JsonObject>().Where(entry => string.Equals(entry["guid"]?.GetValue<string>(), guid.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidDataException("Duplicate plugin GUID in previous catalog.");
        var entry = matches.SingleOrDefault();
        if (entry is not null && entry["versions"] is not JsonArray) throw new InvalidDataException("Invalid previous versions. Refusing to erase history.");
        var versions = entry?["versions"]?.AsArray() ?? [];
        if (versions.Count > 1000) throw new InvalidDataException("Release history exceeds its safe size.");
        if (previousManifest is not null && (entry is null || versions.Count == 0))
            throw new InvalidDataException("Previous manifest has no Media Hub release history. Refusing to replace it.");
        foreach (var row in versions)
        {
            if (row is not JsonObject || !VersionPattern().IsMatch(row["version"]?.GetValue<string>() ?? "") || !Version.TryParse(row["version"]?.GetValue<string>(), out _))
                throw new InvalidDataException("Invalid previous version history.");
            if (row["version"]!.GetValue<string>() == version) throw new InvalidOperationException("Published versions are immutable; choose a new version.");
            if (Version.Parse(row["version"]!.GetValue<string>()) >= parsed)
                throw new InvalidOperationException("New releases must increase the published version; repository history cannot move backwards.");
            var checksum = row["checksum"]?.GetValue<string>() ?? "";
            var previousVersion = row["version"]!.GetValue<string>();
            var expectedSource = $"https://github.com/{repository}/releases/download/plugin-v{previousVersion}/Jellyfin.Plugin.MediaHub_{previousVersion}.zip";
            if (checksum.Length != 32 || !checksum.All(Uri.IsHexDigit) ||
                row["sourceUrl"]?.GetValue<string>() != expectedSource ||
                !Version.TryParse(row["targetAbi"]?.GetValue<string>(), out _) ||
                !DateTimeOffset.TryParse(row["timestamp"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                throw new InvalidDataException("Incomplete or corrupt previous release metadata. Refusing to erase history.");
        }
        if (versions.Select(row => row!["version"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() != versions.Count)
            throw new InvalidDataException("Duplicate published versions in previous catalog.");
        var tag = "plugin-v" + version;
        var archiveName = "Jellyfin.Plugin.MediaHub_" + version + ".zip";
        var source = $"https://github.com/{repository}/releases/download/{tag}/{archiveName}";
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Use an empty output directory; existing files will not be overwritten.");
        var archive = Path.Combine(output, archiveName);
        using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var name in RuntimeFiles.Order(StringComparer.Ordinal))
            {
                var item = zip.CreateEntry(name, CompressionLevel.Optimal);
                item.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = item.Open(); using var original = File.OpenRead(Path.Combine(input, name)); original.CopyTo(stream);
            }
            foreach (var name in new[] { "LICENSE", "THIRD_PARTY_NOTICES.md" })
            {
                var notice = zip.CreateEntry(name); notice.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var noticeStream = notice.Open();
                using var text = typeof(CatalogBuilder).Assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing license notice.");
                text.CopyTo(noticeStream);
            }
        }
        var bytes = File.ReadAllBytes(archive);
        // MD5 is required by Jellyfin's repository format, not used for authentication.
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        versions.Add(new JsonObject { ["version"] = version, ["targetAbi"] = targetAbi, ["sourceUrl"] = source,
            ["checksum"] = md5, ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["changelog"] = "Media Hub plugin preview. See release notes for supported features, setup and remaining acceptance gates." });
        var ordered = versions.Select(row => row!.DeepClone()).OrderByDescending(row => Version.Parse(row["version"]!.GetValue<string>())).ToList();
        if (entry is null) { entry = new JsonObject(); catalog.Add(entry); }
        foreach (var field in new[] { "guid", "name", "overview", "description", "category" }) entry[field] = metadata[field]!.DeepClone();
        entry["owner"] = repository.Split('/')[0];
        entry["versions"] = new JsonArray(ordered.ToArray());
        var manifest = Path.Combine(output, "manifest.json");
        File.WriteAllText(manifest, catalog.ToJsonString(new() { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "SHA256SUMS"), sha256 + "  " + archiveName + "\n", new UTF8Encoding(false));
        var parts = repository.Split('/');
        var repositoryUrl = $"https://{parts[0].ToLowerInvariant()}.github.io/{parts[1]}/manifest.json";
        var html = $"""
            <!doctype html><html lang="en"><meta charset="utf-8"><title>Media Hub plugin repository</title>
            <h1>Media Hub plugin repository · Preview</h1><p>In-process media and Discord integration for Jellyfin 12. Preview features and live-service acceptance remain incomplete.</p>
            <p>Add this manifest URL in Jellyfin Dashboard → Plugins → Repositories:</p>
            <p><a href="manifest.json">{WebUtility.HtmlEncode(repositoryUrl)}</a></p>
            <p><a href="{WebUtility.HtmlEncode(source)}">Download {WebUtility.HtmlEncode(version)}</a> · <a href="SHA256SUMS">SHA-256 checksum</a></p>
            <p>Install Jellyfin-12.2-compatible File Transformation and Plugin Pages from their upstream repository, then restart. They are required for native Web controls and user-facing pages, but are not bundled.</p>
            <p><a href="https://github.com/{WebUtility.HtmlEncode(repository)}/blob/{tag}/plugin/README.md">Features, limitations and setup</a></p></html>
            """;
        File.WriteAllText(Path.Combine(output, "index.html"), html, new UTF8Encoding(false));
        return new(archive, manifest, sha256);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (args.Length % 2 != 0) throw new ArgumentException("Use --input DIR --output DIR --metadata FILE --repository OWNER/REPO --version A.B.C.D [--previous FILE].");
            var allowed = new[] { "--input", "--output", "--metadata", "--repository", "--version", "--previous" };
            for (var i = 0; i < args.Length; i += 2)
                if (!allowed.Contains(args[i]) || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Unknown or repeated option.");
            foreach (var required in allowed[..5]) if (!options.ContainsKey(required)) throw new ArgumentException("Missing option: " + required);
            var result = CatalogBuilder.Create(options["--input"], options["--output"], options["--metadata"], options["--repository"], options["--version"], options.GetValueOrDefault("--previous"));
            Console.WriteLine("Created " + result.Archive); Console.WriteLine("Created " + result.Manifest); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("Packaging failed: " + error.Message); return 1; }
    }
}
