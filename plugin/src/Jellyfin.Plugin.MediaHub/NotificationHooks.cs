using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>A bounded notification-only wake-up. Payloads never supply identities, statuses, URLs or write authority.</summary>
public sealed class NotificationSignals
{
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, long> nonces = new(StringComparer.Ordinal);
    private DateTimeOffset lastWake;
    public bool Accept(string timestamp, string nonce, string signature, byte[] body, string key, DateTimeOffset now)
    {
        if (body.Length > 65536 || timestamp.Length > 12 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var at) ||
            timestamp != at.ToString(CultureInfo.InvariantCulture) || at < now.ToUnixTimeSeconds() - 300 || at > now.ToUnixTimeSeconds() + 30 || nonce.Length != 32 || !nonce.All(Uri.IsHexDigit) || signature.Length != 64 || !signature.All(Uri.IsHexDigit)) return false;
        var prefix = Encoding.UTF8.GetBytes("MediaHub.NotificationHook.v1\n" + timestamp + "\n" + nonce + "\n");
        var canonical = new byte[prefix.Length + body.Length]; prefix.CopyTo(canonical, 0); body.CopyTo(canonical, prefix.Length);
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), canonical);
        if (!CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature))) return false;
        lock (gate)
        {
            foreach (var old in nonces.Where(p => p.Value < now.ToUnixTimeSeconds() - 300).Select(p => p.Key).ToArray()) nonces.Remove(old);
            if (nonces.Count >= 1024 || !nonces.TryAdd(nonce.ToLowerInvariant(), at)) return false;
            if (now - lastWake >= TimeSpan.FromMinutes(1)) { lastWake = now; if (wake.CurrentCount == 0) wake.Release(); }
            return true;
        }
    }
    public Task<bool> Wait(TimeSpan interval, CancellationToken cancellation) => wake.WaitAsync(interval, cancellation);
}

[ApiController, Route("MediaHub/Notifications/Hook"), AllowAnonymous]
[RequestSizeLimit(65536), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationHookController(SettingsService settings, NotificationSignals signals) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellation)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var config = settings.Current;
            if (!Request.IsHttps || config.Features.NotificationHookKeyEncrypted is not { Length: > 0 } secret) return Unauthorized();
            string Header(string name) => Request.Headers.TryGetValue(name, out var value) && value.Count == 1 ? value.ToString() : "";
            var at = Header("X-MediaHub-Timestamp"); var nonce = Header("X-MediaHub-Nonce"); var signature = Header("X-MediaHub-Signature");
            if (at.Length > 12 || nonce.Length != 32 || signature.Length != 64) return Unauthorized();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(5));
            using var body = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await Request.Body.ReadAsync(buffer, budget.Token)) != 0) { if (body.Length + count > 65536) return StatusCode(413); body.Write(buffer, 0, count); }
            if (settings.Current.Revision != config.Revision || !signals.Accept(at, nonce, signature, body.ToArray(), settings.Unprotect(secret), DateTimeOffset.UtcNow)) return Unauthorized();
            return Accepted(new { Queued = true, NotificationReadsOnly = true });
        }
        catch { return StatusCode(503, new { Error = "Notification wake-up unavailable. Regular polling remains enabled." }); }
    }
}
