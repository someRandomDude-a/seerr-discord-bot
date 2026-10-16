using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>No redirects, credential-bearing default headers, automatic application retries or unbounded bodies.</summary>
public sealed class ServiceHttp : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(6);
    public ServiceHttp() : this(ServiceAddress.Handler()) { }
    internal ServiceHttp(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    internal static string BaseUrl(string value)
    {
        return ServiceAddress.Url(value);
    }
    public async Task<JsonNode> Json(HttpMethod method, string origin, string path, string key, JsonNode? body, CancellationToken cancellation)
    {
        ServiceAddress.Route(path); ServiceAddress.Key(key);
        await slots.WaitAsync(cancellation);
        try
        {
            using var request = new HttpRequestMessage(method, BaseUrl(origin) + "/" + path); request.Headers.Add("X-Api-Key", key);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, cancellation); response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync(cancellation);
            return text.Length == 0 ? new JsonObject() : SafeJson.Parse(text);
        }
        finally { slots.Release(); }
    }
    public void Dispose() { http.Dispose(); slots.Dispose(); }
}
