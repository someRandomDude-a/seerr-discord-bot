using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Route("MediaHub/Link")]
[RequestSizeLimit(4096), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class LinkController(IServiceProvider services) : ControllerBase
{
    private LinkFlow links => services.GetRequiredService<LinkFlow>();
    private SettingsService settings => services.GetRequiredService<SettingsService>();
    public sealed record Handoff(string Code);
    private ContentResult Page(string message, string? handoff)
    {
        var nonce = HubStore.Nonce(); Response.Headers["Cache-Control"] = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'nonce-" + nonce + "'; base-uri 'none'; frame-ancestors 'none'";
        var script = handoff is null ? "history.replaceState(null, '', location.pathname);" :
            "history.replaceState(null, '', location.pathname); try { sessionStorage.setItem('mediahub.handoff', " + JsonSerializer.Serialize(handoff) + "); sessionStorage.setItem('mediahub.resume', '1'); } catch {} location.replace(" + JsonSerializer.Serialize(links.WebUrl(handoff)) + ");";
        return Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Media Hub linking</title></head><body><p>" + message + "</p><script nonce=\"" + nonce + "\">" + script + "</script></body></html>", "text/html");
    }
    [HttpGet, AllowAnonymous]
    public IActionResult Landing(string handoff)
    {
        if (handoff.Length != 48 || handoff.Any(c => !char.IsAsciiHexDigit(c))) return Page("This link is invalid. Request a new link yourself in Discord.", null);
        try { return Page("Continue in Jellyfin. Sign in if needed, then verify both identities.", handoff); }
        catch { return Page("Link verification is unavailable. Nothing was linked.", null); }
    }
    private string CookiePath => new Uri(settings.Current.PublicJellyfinUrl).AbsolutePath.TrimEnd('/') + "/MediaHub/Link/OAuth/Callback";
    [HttpPost("Handoff/Begin"), Authorize]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Begin(Handoff request, CancellationToken cancellation)
    {
        try
        {
            var origin = new Uri(settings.Current.PublicJellyfinUrl);
            if (!Request.IsHttps || !string.Equals(Request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase) || (Request.Host.Port ?? 443) != origin.Port)
                throw new HubError("Open your link on the configured HTTPS Jellyfin address, or use explicit code approval.");
            var result = await links.Begin(User, request.Code, cancellation);
            Response.Cookies.Append(LinkFlow.CookieName(result.State), result.Cookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = CookiePath, Expires = result.Expires, IsEssential = true });
            return Ok(result);
        }
        catch (HubError e) { return BadRequest(new { Error = e.Message }); }
        catch { return StatusCode(503, new { Error = "Link verification unavailable. Nothing was linked; use explicit approval or try later." }); }
    }
    [HttpGet("OAuth/Callback"), AllowAnonymous]
    public async Task<IActionResult> Callback(string? state, string? code, CancellationToken cancellation)
    {
        if (state is null || code is null) return Page("Discord verification was not completed. Reopen your self-requested link.", null);
        try
        {
            var handoff = await links.Callback(state, code, Request.Cookies[LinkFlow.CookieName(state)] ?? "", cancellation);
            Response.Cookies.Delete(LinkFlow.CookieName(state), new CookieOptions { Path = CookiePath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
            return Page("Discord verified. Return to Jellyfin to review and finish linking.", handoff);
        }
        catch { return Page("Both browser identities could not be verified. Reopen your self-requested link. No account was linked.", null); }
    }
    [HttpPost("Handoff/Status"), Authorize]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Status(Handoff request)
    {
        try { return Ok(await links.Status(User, request.Code)); }
        catch (HubError e) { return BadRequest(new { Error = e.Message }); }
        catch { return StatusCode(503, new { Error = "Link status unavailable. Nothing was linked." }); }
    }
    [HttpPost("Handoff/Complete"), Authorize]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Complete(Handoff request, CancellationToken cancellation)
    {
        try { await links.Complete(User, request.Code, cancellation); return Ok(new { Linked = true }); }
        catch (HubError e) { return BadRequest(new { Error = e.Message }); }
        catch { return StatusCode(503, new { Error = "Link was not confirmed. Check Media Hub before retrying." }); }
    }
}
