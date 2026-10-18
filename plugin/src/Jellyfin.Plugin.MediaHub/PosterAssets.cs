using System.Buffers.Binary;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Fixed TMDB artwork only. No caller-selected URL, credentials, redirects or active image formats.</summary>
public sealed class PosterAssets : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim slots = new(2);
    public PosterAssets() : this(ServiceAddress.Handler()) { }
    internal PosterAssets(HttpMessageHandler handler) => http = new(handler) { Timeout = TimeSpan.FromSeconds(10) };
    internal static string Mime(byte[] data)
    {
        int width = 0, height = 0; string mime;
        if (data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        { width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)); height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)); mime = "image/png"; }
        else if (data.Length >= 4 && data[0] == 255 && data[1] == 216)
        {
            mime = "image/jpeg"; var offset = 2;
            while (offset + 4 <= data.Length)
            {
                if (data[offset++] != 255) throw new HubError("Invalid artwork encoding.");
                while (offset < data.Length && data[offset] == 255) offset++;
                if (offset >= data.Length) break; var marker = data[offset++]; if (marker is 217 or 218) break;
                if (marker is 1 or >= 208 and <= 216) continue;
                if (offset + 2 > data.Length) break; var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                if (size < 2 || offset + size > data.Length) throw new HubError("Invalid artwork encoding.");
                if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204))
                {
                    if (size < 8) throw new HubError("Invalid artwork dimensions.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2)); width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5, 2)); break;
                }
                offset += size;
            }
        }
        else throw new HubError("Only bounded PNG/JPEG posters are supported.");
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || (long)width * height > 4_000_000) throw new HubError("Artwork dimensions exceed their safe limit.");
        return mime;
    }
    public async Task<MessageBlob> Download(string path, CancellationToken cancellation)
    {
        var url = DiscordHost.DiscordPoster(path) ?? throw new HubError("No verified poster is available.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(10)); cancellation = budget.Token;
        await slots.WaitAsync(cancellation);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new HubError("Artwork exceeds 2 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation); using var bytes = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0) { if (bytes.Length + count > 2 * 1024 * 1024) throw new HubError("Artwork exceeds 2 MiB."); bytes.Write(buffer, 0, count); }
            var data = bytes.ToArray(); return new(data, "poster", Mime(data));
        }
        finally { slots.Release(); }
    }
    public void Dispose() { http.Dispose(); slots.Dispose(); }
}
