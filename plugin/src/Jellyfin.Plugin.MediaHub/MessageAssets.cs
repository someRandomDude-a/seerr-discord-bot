namespace Jellyfin.Plugin.MediaHub;

/// <summary>Only retained Discord CDN URLs. Authentication is never forwarded to the CDN; redirects remain disabled.</summary>
public sealed class MessageAssets : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(4);
    public MessageAssets() : this(ServiceAddress.Handler()) { }
    internal MessageAssets(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
    internal static bool Allowed(string url)
    {
        if (url is null || url.Length > 4096 || url.Any(c => char.IsControl(c) || c == '\\') || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net")) return false;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        return System.Text.RegularExpressions.Regex.IsMatch(path, "\\A/(attachments|avatars|embed/avatars|emojis)/[A-Za-z0-9_./-]+\\z") && !path.Contains("..");
    }
    public async Task<MessageBlob> Download(string url, string filename, CancellationToken cancellation)
    {
        if (!Allowed(url)) throw new HubError("Only retained Discord CDN attachments can be fetched.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(10)); cancellation = budget.Token;
        await slots.WaitAsync(cancellation);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > Messaging.FileLimit) throw new HubError("Attachment exceeds 8 MiB.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation); using var body = new MemoryStream(); var buffer = new byte[65536];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) != 0)
            { if (body.Length + read > Messaging.FileLimit) throw new HubError("Attachment exceeds 8 MiB."); body.Write(buffer, 0, read); }
            var bytes = body.ToArray(); return new(bytes, Messaging.Filename(filename), Messaging.Mime(bytes));
        }
        finally { slots.Release(); }
    }
    public void Dispose() { http.Dispose(); slots.Dispose(); }
}
