using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ConfigurationChoice(string Value, string Label);
public sealed record ServiceConfigurationOptions(IReadOnlyList<ConfigurationChoice> Roots, IReadOnlyList<ConfigurationChoice> QualityProfiles, IReadOnlyList<ConfigurationChoice> MetadataProfiles);
public sealed record DiscoveredDestination(string Service, int Instance, string Url, bool Approved);

/// <summary>Elevated, read-only option discovery from already saved authenticated addresses; never probes a client-supplied URL.</summary>
public sealed class ConfigurationDiscovery(SettingsService settings, ServiceHttp http)
{
    private HubSettings Snapshot(int revision)
    {
        var s = settings.Current;
        if (s.Revision != revision) throw new HubError("Server settings changed. Reload and review before loading service options.");
        return s;
    }
    private static string Text(JsonNode? node, string field, int max)
    {
        var value = node?[field]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw new HubError("The service returned invalid configuration labels.");
        return value;
    }
    private static IReadOnlyList<ConfigurationChoice> Profiles(JsonNode value)
    {
        var rows = value.AsArray(); if (rows.Count > 1000) throw new HubError("Too many service profiles.");
        var seen = new HashSet<int>(); var result = new List<ConfigurationChoice>();
        foreach (var row in rows)
        {
            var id = row?["id"]?.GetValue<int>() ?? 0;
            if (id <= 0 || !seen.Add(id)) throw new HubError("Ambiguous service profile identity.");
            result.Add(new(id.ToString(System.Globalization.CultureInfo.InvariantCulture), Text(row, "name", 256)));
        }
        return result;
    }
    public async Task<ServiceConfigurationOptions> Options(string service, int revision, CancellationToken cancellation)
    {
        var s = Snapshot(revision);
        var config = service switch { "lidarr" => s.Features.Lidarr, "readarr" => s.Features.Readarr, _ => throw new HubError("Choose Lidarr or Readarr.") };
        if (config.Url.Length == 0 || config.KeyEncrypted.Length == 0) throw new HubError("Save the service address and API key before loading its options.");
        var key = settings.Unprotect(config.KeyEncrypted);
        Task<JsonNode> Get(string route) { Snapshot(revision); return http.Json(HttpMethod.Get, config.Url, "api/v1/" + route, key, null, cancellation); }
        var roots = (await Get("rootfolder")).AsArray(); if (roots.Count > 1000) throw new HubError("Too many service root folders.");
        var paths = new HashSet<string>(StringComparer.Ordinal); var choices = new List<ConfigurationChoice>();
        foreach (var root in roots)
        {
            var path = Text(root, "path", 2048); if (!paths.Add(path)) throw new HubError("Ambiguous service root folder.");
            if (root?["accessible"]?.GetValue<bool>() == false) continue;
            choices.Add(new(path, path));
        }
        var quality = Profiles(await Get("qualityprofile")); var metadata = Profiles(await Get("metadataprofile"));
        Snapshot(revision); return new(choices, quality, metadata);
    }
    public async Task<IReadOnlyList<DiscoveredDestination>> Destinations(int revision, CancellationToken cancellation)
    {
        var s = Snapshot(revision);
        if (s.SeerrUrl.Length == 0 || s.SeerrKeyEncrypted.Length == 0) throw new HubError("Save the Seerr address and API key before finding its configured servers.");
        var key = settings.Unprotect(s.SeerrKeyEncrypted); var result = new List<DiscoveredDestination>();
        foreach (var service in new[] { "radarr", "sonarr" })
        {
            Snapshot(revision);
            // Only Seerr is contacted. A discovered server is NEVER probed or approved here.
            var rows = (await http.Json(HttpMethod.Get, s.SeerrUrl, "api/v1/settings/" + service, key, null, cancellation)).AsArray();
            if (rows.Count > 100) throw new HubError("Too many configured media servers.");
            var ids = new HashSet<int>();
            foreach (var row in rows)
            {
                var id = row?["id"]?.GetValue<int>() ?? -1; var port = row?["port"]?.GetValue<int>() ?? 0;
                if (id < 0 || !ids.Add(id)) throw new HubError("Ambiguous media-server instance identity.");
                var url = ServiceAddress.Discovered(Text(row, "hostname", 512), port, row?["baseUrl"]?.GetValue<string>() ?? "", row?["useSsl"]?.GetValue<bool>() == true);
                result.Add(new(service, id, url, s.Features.ArrDestinations.Any(d => d.Service == service && d.Instance == id && ServiceAddress.Url(d.Url) == url)));
            }
        }
        Snapshot(revision); return result;
    }
}
