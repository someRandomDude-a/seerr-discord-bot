using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, Authorize, Route("MediaHub/Messaging")]
[RequestSizeLimit(65536), ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MessageController(IServiceProvider services) : ControllerBase
{
    private HostIdentity identity => services.GetRequiredService<HostIdentity>();
    private OperatorAuthority operators => services.GetRequiredService<OperatorAuthority>();
    private Messaging messages => services.GetRequiredService<Messaging>();
    private MessageAssets assets => services.GetRequiredService<MessageAssets>();
    private async Task<OperatorActor> Actor(CancellationToken cancellation) => await operators.Require((await identity.Browser(User)).UserId, cancellation);
    private async Task<IActionResult> Safe(Func<Task<object>> action)
    {
        Response.Headers["Cache-Control"] = "no-store";
        try { var result = await action(); return result is IActionResult response ? response : Ok(result); }
        catch (HubError e) { return StatusCode(e.Code == "operator_denied" ? 403 : 400, new { Error = e.Message }); }
        catch { return StatusCode(503, new { Error = "Not confirmed. Check the result before retrying." }); }
    }
    [HttpGet("Access")]
    public Task<IActionResult> Access(CancellationToken cancellation) => Safe(async () => { await Actor(cancellation); return new { Operator = true }; });
    [HttpGet("Destinations")]
    public Task<IActionResult> Destinations(CancellationToken cancellation) => Safe(async () => await messages.Destinations(await Actor(cancellation), cancellation));
    [HttpGet("Inbox")]
    public Task<IActionResult> Inbox(CancellationToken cancellation, string kind = "all", string? guild = null, string? peer = null, string? channel = null, string query = "", int page = 1, bool oldest = false) => Safe(async () =>
        await messages.Inbox(await Actor(cancellation), kind, guild, peer, channel, query, page, oldest, cancellation));
    [HttpGet("History")]
    public Task<IActionResult> History(CancellationToken cancellation, int page = 1) => Safe(async () => await messages.History(await Actor(cancellation), page, cancellation));
    [HttpGet("Inbox/{id}")]
    public Task<IActionResult> Message(string id, CancellationToken cancellation) => Safe(async () => await messages.Message(await Actor(cancellation), id, cancellation));
    [HttpGet("Diagnostics")]
    public Task<IActionResult> Diagnostics(CancellationToken cancellation) => Safe(async () => await messages.Diagnostics(await Actor(cancellation), cancellation));
    public sealed record Compose(string Mode, string Content, MessageTarget[] Targets, string[] Uploads, string? RequestId = null, bool Members = false);
    [HttpPost("Prepare")]
    [RequestSizeLimit(65536)]
    public Task<IActionResult> Prepare(Compose request, CancellationToken cancellation) => Safe(async () =>
        await messages.Prepare(await Actor(cancellation), request.Mode, request.Content, request.Targets, request.Uploads, request.RequestId, request.Members, cancellation));
    [HttpPost("Chat")]
    [RequestSizeLimit(65536)]
    public Task<IActionResult> Chat(Compose request, CancellationToken cancellation) => Safe(async () =>
    {
        if (request.Mode != "normal" || request.Members) throw new HubError("Broadcasts require an announcement preview.");
        var actor = await Actor(cancellation); var plan = await messages.Prepare(actor, "normal", request.Content, request.Targets, request.Uploads, request.RequestId, false, cancellation);
        return await messages.Confirm(actor, plan.Id, cancellation);
    });
    [HttpPost("Plans/{id}/Confirm")]
    public Task<IActionResult> Confirm(string id, CancellationToken cancellation) => Safe(async () => await messages.Confirm(await Actor(cancellation), id, cancellation));
    [HttpPost("Plans/{id}/Cancel")]
    public Task<IActionResult> Cancel(string id, CancellationToken cancellation) => Safe(async () => { await messages.Cancel(await Actor(cancellation), id, cancellation); return new { Cancelled = true }; });
    [HttpGet("Plans/{id}")]
    public Task<IActionResult> Receipt(string id, CancellationToken cancellation) => Safe(async () => await messages.Receipt(await Actor(cancellation), id, cancellation));
    [HttpPost("Uploads")]
    [RequestSizeLimit(Messaging.FileLimit + 65536), RequestFormLimits(MultipartBodyLengthLimit = Messaging.FileLimit + 65536)]
    public Task<IActionResult> Upload(IFormFile file, CancellationToken cancellation) => Safe(async () =>
    {
        var actor = await Actor(cancellation); if (file.Length is 0 or > Messaging.FileLimit) throw new HubError("Uploads are limited to 8 MiB each.");
        using var body = new MemoryStream(); await file.CopyToAsync(body, cancellation); return await messages.Upload(actor, file.FileName, body.ToArray(), cancellation);
    });
    private IActionResult Blob(MessageBlob blob, bool preview)
    {
        Response.Headers["X-Content-Type-Options"] = "nosniff"; Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        return preview && blob.Mime == "image/png" ? File(blob.Bytes, blob.Mime) : File(blob.Bytes, "application/octet-stream", blob.Filename);
    }
    [HttpGet("Uploads/{id}")]
    public Task<IActionResult> UploadBody(string id, CancellationToken cancellation, bool preview = false) => Safe(async () => Blob(await messages.DownloadUpload(await Actor(cancellation), id, cancellation), preview));
    [HttpGet("Inbox/{id}/Assets/{key}")]
    public Task<IActionResult> Asset(string id, string key, CancellationToken cancellation, bool preview = false) => Safe(async () =>
    {
        var actor = await Actor(cancellation);
        if (key.StartsWith("upload:", StringComparison.Ordinal)) return Blob(await messages.SentUpload(actor, id, key["upload:".Length..], cancellation), preview);
        var before = await messages.Asset(actor, id, key, cancellation);
        var body = await assets.Download(before.Url, before.Filename, cancellation); var after = await messages.Asset(actor, id, key, cancellation);
        if (before != after) throw new HubError("Attachment changed or was removed. Refresh first.");
        return Blob(body, preview);
    });
}
