namespace Jellyfin.Plugin.MediaHub;

/// <summary>Only retained Discord CDN URLs. Authentication is never forwarded to the CDN; redirects remain disabled.</summary>
public sealed class MessageAssets : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(4);
    public MessageAssets() : this(ServiceAddress.Handler()) { }
    internal MessageAssets(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
    internal static bool Allowed(string url)
        => Jellyfin.Plugin.WatchlistDeletion.Api.MediaPresentation.DiscordAsset(url);
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
