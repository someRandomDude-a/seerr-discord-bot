using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ArrEndpoint(string Name, int Id, string Url, [property: JsonIgnore] string Key, string Version);
public sealed record ServiceSpace(string Service, int Instance, bool Verified, long? Free, long? Total);

/// <summary>Administrative keys are used only for discovery and service calls, never returned to a viewer.</summary>
public sealed class ArrRegistry(SettingsService settings, ServiceHttp http)
{
    public async Task<IReadOnlyList<ArrEndpoint>> Read(CancellationToken cancellation)
    {
        var s = settings.Current; var results = new List<ArrEndpoint>();
        if (s.SeerrUrl.Length > 0 && s.SeerrKeyEncrypted.Length > 0)
        {
            foreach (var name in new[] { "radarr", "sonarr" })
            {
                var rows = (await http.Json(HttpMethod.Get, s.SeerrUrl, "api/v1/settings/" + name, settings.Unprotect(s.SeerrKeyEncrypted), null, cancellation)).AsArray();
                if (rows.Count > 100) throw new HubError("Service discovery is too large.");
                var ids = new HashSet<int>();
                foreach (var row in rows)
                {
                    var id = row!["id"]!.GetValue<int>(); var host = row["hostname"]!.GetValue<string>(); var port = row["port"]!.GetValue<int>(); var basePath = row["baseUrl"]?.GetValue<string>() ?? "";
                    if (id < 0 || !ids.Add(id)) throw new HubError("Invalid or ambiguous service destination.");
                    var url = ServiceAddress.Discovered(host, port, basePath, row["useSsl"]?.GetValue<bool>() == true);
                    var key = row["apiKey"]!.GetValue<string>(); if (key.Length == 0) throw new HubError("Service key unavailable.");
                    settings.RequireArr(name, id, url); ServiceAddress.Key(key);
                    results.Add(new(name, id, ServiceHttp.BaseUrl(url), key, "v3"));
                }
            }
        }
        foreach (var pair in new[] { ("lidarr", s.Features.Lidarr), ("readarr", s.Features.Readarr) })
            if (pair.Item2.Url.Length > 0) results.Add(new(pair.Item1, 0, ServiceHttp.BaseUrl(pair.Item2.Url), settings.Unprotect(pair.Item2.KeyEncrypted), "v1"));
        return results;
    }
    public async Task<IReadOnlyList<ServiceSpace>> Storage(CancellationToken cancellation)
    {
        var endpoints = await Read(cancellation);
        var tasks = endpoints.Select(async endpoint =>
        {
            try
            {
                var rows = (await http.Json(HttpMethod.Get, endpoint.Url, "api/" + endpoint.Version + "/diskspace", endpoint.Key, null, cancellation)).AsArray();
                if (rows.Count is 0 or > 1000) throw new HubError("Invalid disk scan.");
                long free = 0, total = 0;
                foreach (var row in rows)
                {
                    var f = row!["freeSpace"]!.GetValue<long>(); var t = row["totalSpace"]!.GetValue<long>();
                    if (f < 0 || t < f) throw new HubError("Invalid storage values.");
                    free = checked(free + f); total = checked(total + t);
                }
                return new ServiceSpace(endpoint.Name, endpoint.Id, true, free, total);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { return new ServiceSpace(endpoint.Name, endpoint.Id, false, null, null); }
        });
        return await Task.WhenAll(tasks); // Values stay per-service; do not pretend shared mounts are independent capacity.
    }
}
