using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Bounds and ambiguity checks shared by network, configuration and retained-data readers.</summary>
internal static class SafeJson
{
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static void Validate(ReadOnlySpan<byte> bytes, int maximum = 8 * 1024 * 1024)
    {
        if (bytes.Length is 0 || bytes.Length > maximum) throw new HubError("JSON input exceeds its supported size or is empty.");
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
            var objects = new Stack<HashSet<string>>(); var tokens = 0;
            while (reader.Read())
            {
                if (++tokens > 250000 || reader.TokenType == JsonTokenType.String && reader.ValueSpan.Length > 131072)
                    throw new HubError("JSON input is too complex.");
                if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.OrdinalIgnoreCase));
                else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName &&
                    (reader.ValueSpan.Length > 512 || !objects.Peek().Add(reader.GetString()!)))
                    throw new HubError("Ambiguous or oversized JSON field names are not accepted.");
            }
            if (tokens == 0) throw new HubError("Empty JSON input.");
        }
        catch (JsonException) { throw new HubError("Invalid JSON input. No operation was authorized."); }
    }
    public static JsonNode Parse(string text, int maximum = 8 * 1024 * 1024)
    {
        if (text is null || text.Length > maximum) throw new HubError("JSON input exceeds its supported size.");
        Validate(Encoding.UTF8.GetBytes(text), maximum);
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) ?? throw new HubError("JSON null is not a service response.");
    }
    public static T Read<T>(string text, int maximum = 65536)
    {
        _ = Parse(text, maximum);
        try { return JsonSerializer.Deserialize<T>(text, Options) is { } result ? result : throw new HubError("Invalid retained JSON data."); }
        catch (JsonException) { throw new HubError("Invalid retained JSON data. No operation was authorized."); }
    }
}

/// <summary>Only explicit HTTP(S) destinations and non-escaping API routes. Private service addresses remain supported.</summary>
internal static class ServiceAddress
{
    public static HttpClientHandler Handler() => new() { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 4, MaxResponseHeadersLength = 32 };
    public static string Url(string? value, bool https = false)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(c => char.IsControl(c) || c == '\\') || value != value.Trim() ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || (https ? uri.Scheme != "https" : uri.Scheme is not ("http" or "https")) ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Contains('%'))
            throw new HubError("Use an explicit HTTP(S) service address without credentials, query, fragment or ambiguous escapes.");
        var pathStart = value.IndexOf('/', value.IndexOf("://", StringComparison.Ordinal) + 3);
        if (pathStart >= 0) Path(value[pathStart..]); // Inspect the original, before System.Uri normalizes dot segments.
        return uri.AbsoluteUri.TrimEnd('/');
    }
    private static void Path(string path)
    {
        foreach (var part in path.Split('/'))
        {
            for (var i = 0; i < part.Length; i++) if (part[i] == '%' && (i + 2 >= part.Length || !Uri.IsHexDigit(part[i + 1]) || !Uri.IsHexDigit(part[i + 2]))) throw new HubError("Invalid service path escape.");
            var decoded = Uri.UnescapeDataString(part);
            if (decoded is "." or ".." || decoded.Any(c => char.IsControl(c) || c is '/' or '\\' or '?' or '#' or '%' or ':' or '@'))
                throw new HubError("Escaping or ambiguous service paths are not accepted.");
        }
        if (path.Contains("//", StringComparison.Ordinal)) throw new HubError("Ambiguous service path.");
    }
    public static string Discovered(string host, int port, string basePath, bool ssl)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or '?' or '#' or '%') ||
            Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown || port is < 1 or > 65535 || basePath is null || basePath.Length > 1024)
            throw new HubError("Invalid discovered service destination.");
        Path("/" + basePath.Trim('/'));
        return Url(new UriBuilder(ssl ? "https" : "http", host, port, "/" + basePath.Trim('/')).Uri.AbsoluteUri);
    }
    public static void Key(string? key)
    { if (string.IsNullOrEmpty(key) || key.Length > 4096 || key.Any(c => c is < '!' or > '~')) throw new HubError("Invalid service credential format."); }
    public static void Route(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 8192 || path.StartsWith('/') || path.Any(c => char.IsControl(c) || c is '\\' or '#')) throw new HubError("Invalid service route.");
        Path(path.Split('?')[0]);
    }
}
