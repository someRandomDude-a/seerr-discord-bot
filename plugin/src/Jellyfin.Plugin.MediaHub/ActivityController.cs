using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

[ApiController, AllowAnonymous, Route("MediaHub/Activity")]
[ServiceFilter(typeof(HubRequestBoundary), Order = int.MinValue)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ActivityController(IServiceProvider services) : ControllerBase
{
    private T Service<T>() where T : notnull => services.GetRequiredService<T>();
    private string Token => Request.Headers["X-MediaHub-Session"].ToString();
    private string Peer => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "local";
    private void Origin()
    {
        var value = Request.Headers.Origin.ToString(); if (value.Length == 0) return;
        var s = Service<SettingsService>().Current;
        var native = new Uri(s.PublicJellyfinUrl).GetLeftPart(UriPartial.Authority);
        if (value != native && value != "https://" + s.Features.DiscordApplicationId + ".discordsays.com") throw new HubError("Activity origin denied.", "activity_denied");
    }
    private async Task<IActionResult> Safe(Func<Task<object>> operation)
    {
        Response.Headers["Cache-Control"] = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer"; Response.Headers["X-Content-Type-Options"] = "nosniff";
        try { Origin(); var result = await operation(); return result is IActionResult response ? response : Ok(result); }
        catch (HubError e) { return StatusCode(e.Code == "activity_denied" ? 401 : e.Code == "operator_denied" ? 403 : 400, new { Error = e.Message }); }
        catch { return StatusCode(503, new { Error = "Verification unavailable. No automatic retry was performed." }); }
    }
    [HttpGet("Config")]
    public Task<IActionResult> Config() => Safe(() => { var s = Service<SettingsService>().Current; if (!s.Features.ActivityEnabled) throw new HubError("Activity is disabled.", "activity_denied"); return Task.FromResult<object>(new { ApplicationId = s.Features.DiscordApplicationId }); });
    [HttpPost("Challenge"), RequestSizeLimit(4096)]
    public Task<IActionResult> NewChallenge() => Safe(() => Task.FromResult<object>(new { Challenge = Service<ActivitySessions>().Challenge(Peer) }));
    public sealed record Authentication(string Challenge, string Code, string Guild);
    [HttpPost("Authenticate"), Consumes("application/json"), RequestSizeLimit(4096)]
    public Task<IActionResult> Authenticate(Authentication request, CancellationToken cancellation) => Safe(async () => await Service<ActivitySessions>().Authenticate(request.Challenge, request.Code, request.Guild, Peer, cancellation));
    [HttpGet("Profile")]
    public Task<IActionResult> Profile(CancellationToken cancellation) => Safe(async () => await Service<ActivitySessions>().Profile(Token, cancellation));
    [HttpPost("Link"), RequestSizeLimit(4096)]
    public Task<IActionResult> Link(CancellationToken cancellation) => Safe(async () => new { Url = Service<LinkFlow>().HandoffUrl(await Service<ActivitySessions>().Link(Token, cancellation)) });
    [HttpPost("Logout")]
    public Task<IActionResult> Logout() => Safe(() => { Service<ActivitySessions>().Logout(Token); return Task.FromResult<object>(new { LoggedOut = true }); });
    private IActionResult Resource(string name, string mime)
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.MediaHub.Web." + name) ?? throw new HubError("Activity assets are not built. Build plugin/activity before packaging."); return File(stream, mime);
    }
    [HttpGet(""), HttpGet("App")]
    public Task<IActionResult> App() => Safe(() =>
    {
        if (!Service<SettingsService>().Current.Features.ActivityEnabled) throw new HubError("Activity is disabled.", "activity_denied");
        if (!Request.Path.Value!.EndsWith("/", StringComparison.Ordinal) && !Request.Path.Value.EndsWith("/App", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<object>(Redirect(Request.PathBase + Request.Path + "/" + Request.QueryString));
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors https://discord.com https://ptb.discord.com https://canary.discord.com https://*.discordsays.com";
        return Task.FromResult<object>(Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>Media Hub</title><link rel=\"stylesheet\" href=\"Assets/activity.css\"></head><body><main id=\"app\">Verifying Discord…</main><script type=\"module\" src=\"Assets/activity.bundle.js\"></script></body></html>", "text/html"));
    });
    [HttpGet("Shell")]
    public Task<IActionResult> Shell() => Safe(() => Task.FromResult<object>(Resource("hub.html", "text/html")));
    [HttpGet("Assets/{name}")]
    public Task<IActionResult> Asset(string name) => Safe(() =>
    {
        if (name is not ("activity.bundle.js" or "activity.css" or "mediahub.js" or "mediahub.messenger.js")) throw new HubError("Unknown Activity asset.");
        return Task.FromResult<object>(Resource(name switch { "mediahub.js" => "hub.js", "mediahub.messenger.js" => "messenger.js", _ => name }, name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "application/javascript"));
    });
    [HttpGet("Api/{**route}"), HttpPost("Api/{**route}")]
    [RequestSizeLimit(Messaging.FileLimit + 65536), RequestFormLimits(MultipartBodyLengthLimit = Messaging.FileLimit + 65536)]
    public Task<IActionResult> Api(string route, CancellationToken cancellation) => Safe(async () =>
    {
        var context = await Service<ActivitySessions>().Require(Token, cancellation); var user = context.User; var post = HttpMethods.IsPost(Request.Method);
        JsonNode body = new JsonObject();
        if (post && !route.Equals("Messaging/Uploads", StringComparison.Ordinal))
        {
            if (Request.ContentLength > 65536 || !Request.HasJsonContentType()) throw new HubError("Use a bounded JSON operation.");
            using var bytes = new MemoryStream(); var buffer = new byte[4096]; int count;
            while ((count = await Request.Body.ReadAsync(buffer, cancellation)) != 0) { if (bytes.Length + count > 65536) throw new HubError("JSON operation exceeds 64 KiB."); bytes.Write(buffer, 0, count); }
            body = SafeJson.Parse(System.Text.Encoding.UTF8.GetString(bytes.ToArray()), 65536);
        }
        T Read<T>() where T : class => body.Deserialize<T>() ?? throw new HubError("Invalid operation.");
        string Q(string name, string fallback = "") => Request.Query.TryGetValue(name, out var value) ? value.ToString() : fallback;
        int Page() => int.TryParse(Q("page", "1"), out var p) ? p : throw new HubError("Invalid page.");
        async Task Verify() { if ((await Service<ActivitySessions>().Require(Token, cancellation)).User.Id != user.Id) throw new HubError("Activity identity changed.", "activity_denied"); }
        var personal = Service<PersonalService>(); var catalog = Service<MediaCatalog>(); var requests = Service<MediaRequests>(); var extras = Service<ExtraMedia>(); var queue = Service<DeletionQueue>();
        if (route == "Status" && !post) return new { Linked = true, WatchParty = Service<WatchPartyIntegration>().Status(), Party = new { Active = false }, FeatureParityComplete = false, ActualPlayerRequired = true };
        if (route == "Me") { if (!post) return personal.State(user); var update = Read<HubController.PersonalUpdate>(); return personal.Save(user, update.Revision, update.Preferences); }
        if (route == "Catalog/Library" && !post) return catalog.Library(user, Q("kind", "all"), Q("query"), Page());
        if (route == "Catalog/Browse" && !post) return await catalog.Browse(user, Q("kind", "movie"), Q("query"), Page(), cancellation);
        if (route == "Requests" && !post) return await catalog.Requests(user, Q("all") == "true", Page(), cancellation);
        if (route == "Me/RequestPermissions" && !post) return await catalog.Permissions(user, cancellation);
        if (route == "SeerrLink" && !post) { using var scope = await Service<SeerrBridge>().Open(user, cancellation); await Verify(); return new { Url = scope.Viewer.Origin }; }
        if (route == "NativeHub" && !post) { await Verify(); return new { Url = Service<SettingsService>().WebUrl() }; }
        if (route == "Requests/Prepare" && post) { var selection = Read<HubController.RequestSelection>(); return await requests.Prepare(user, selection.Kind, selection.ExternalId, selection.Is4k, selection.Seasons, cancellation); }
        var match = Regex.Match(route, "^Requests/([0-9a-f]{48})/(Confirm|Cancel|Receipt)$");
        if (match.Success) { var id = match.Groups[1].Value; return match.Groups[2].Value switch { "Confirm" when post => await requests.Confirm(user, id, Verify, cancellation), "Cancel" when post => requests.Cancel(user, id), "Receipt" when !post => requests.Receipt(user, id), _ => throw new HubError("Invalid request operation.") }; }
        match = Regex.Match(route, "^Catalog/(movie|tv)/([0-9]+)/Poster$");
        if (match.Success && !post)
        {
            var kind = match.Groups[1].Value; var id = int.Parse(match.Groups[2].Value); var card = await catalog.Details(user, kind, id, cancellation);
            var image = await Service<PosterAssets>().Download(card.Poster ?? "", cancellation); await Verify();
            if ((await catalog.Details(user, kind, id, cancellation)).Poster != card.Poster) throw new HubError("Artwork changed.");
            Response.Headers["X-Content-Type-Options"] = "nosniff"; Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'"; return File(image.Bytes, image.Mime);
        }
        match = Regex.Match(route, "^Catalog/(movie|tv)/([0-9]+)/(Details|Open)$");
        if (match.Success) { var kind = match.Groups[1].Value; var id = int.Parse(match.Groups[2].Value); return match.Groups[3].Value == "Open" && post ? await catalog.Open(user, kind, id, cancellation) : await catalog.Details(user, kind, id, cancellation, post); }
        match = Regex.Match(route, "^Catalog/(movie|tv)/([0-9]+)/Editions(?:/([0-9a-fA-F-]{32,36})/Open)?$");
        if (match.Success) return match.Groups[3].Success && post ? catalog.OpenEdition(user, match.Groups[1].Value, int.Parse(match.Groups[2].Value), Guid.Parse(match.Groups[3].Value)) : !post && !match.Groups[3].Success ? catalog.Editions(user, match.Groups[1].Value, int.Parse(match.Groups[2].Value)) : throw new HubError("Invalid edition operation.");
        match = Regex.Match(route, "^Items/([0-9a-fA-F-]{32,36})/(Activity|DeletionRequests|Details)$");
        if (match.Success) { var id = Guid.Parse(match.Groups[1].Value); if (match.Groups[2].Value == "Details") { if (!post) throw new HubError("Explicit details require POST."); return catalog.NativeDetails(user, id, true); } if (match.Groups[2].Value == "Activity") { if (!post) throw new HubError("Explicit media activity requires POST."); return Service<MediaActivity>().Record(user, id, Read<HubController.Interaction>().Action); } if (post) throw new HubError("Read-only operation."); return await Service<IRequestAuthority>().FindOwned(user, id, cancellation); }
        match = Regex.Match(route, "^Me/Watchlist/([0-9a-fA-F-]{32,36})/(Bell|Remove|Add)$");
        if (match.Success && post) { var id = Guid.Parse(match.Groups[1].Value); if (match.Groups[2].Value == "Remove") return personal.Remove(user, id, Read<HubController.WatchlistRemove>().Revision); if (match.Groups[2].Value == "Add") return personal.Add(user, id, Read<HubController.WatchlistRemove>().Revision); var update = Read<HubController.BellUpdate>(); return personal.Bell(user, id, update.Subscribed, update.Revision); }
        match = Regex.Match(route, "^Me/Watchlist/(movie|tv)/([0-9]+)$");
        if (match.Success && post) { var update = Read<HubController.ExternalWatchUpdate>(); var activity = await Service<WatchlistSync>().Follow(user, new(match.Groups[1].Value, int.Parse(match.Groups[2].Value)), update.Subscribed, update.Revision, Verify, cancellation, update.SaveOnly); return personal.State(user) with { Activity = activity }; }
        if (route == "Me/Watchlist/Sync" && post) return await Service<WatchlistSync>().Sync(user, Verify, cancellation);
        if (route == "Me/Watchlist/Sync/Issues" && !post) return Service<WatchlistSync>().Issues(user);
        if (route == "Me/Watchlist/Sync/Review" && post) return await Service<WatchlistSync>().Review(user, Read<MediaKey>(), Verify, cancellation);
        if (route == "Me/Watchlist/Sync/AcceptRemote" && post) return await Service<WatchlistSync>().AcceptRemote(user, Read<HubController.SyncAcceptance>().Token, Verify, cancellation);
        if (route == "Me/Watchlist/Legacy/Review" && post) return await Service<WatchlistSync>().ReviewLegacy(user, Verify, cancellation);
        if (route == "Me/Watchlist/Legacy/Accept" && post) { await Service<WatchlistSync>().AcceptLegacy(user, Read<HubController.SyncAcceptance>().Token, Verify, cancellation); return new { Saved = true }; }
        if (route == "Me/Notifications" && !post) return await Service<NotificationFeed>().ListAsync(user, cancellation);
        if (route == "Me/Notifications/Diagnostics" && !post) return Service<NotificationFeed>().Diagnostics(user);
        match = Regex.Match(route, "^Me/Notifications/([0-9a-f]{48})/Seen$"); if (match.Success && post) { Service<NotificationFeed>().Seen(user, match.Groups[1].Value); return new { Saved = true }; }
        if (route == "Storage" && !post) { using var scope = await Service<SeerrBridge>().Open(user, cancellation); var spaces = await Service<ArrRegistry>().Storage(cancellation); await scope.Verify(cancellation); return spaces; }
        match = Regex.Match(route, "^Extras/(music|book)/Search$"); if (match.Success && !post) return await extras.Search(user, match.Groups[1].Value, Q("query"), Page(), cancellation);
        match = Regex.Match(route, "^Extras/(music|book)/([^/]+)/Details$"); if (match.Success) return await extras.Details(user, match.Groups[1].Value, match.Groups[2].Value, post, cancellation);
        if (route == "Extras/Requests/Prepare" && post) { var selection = Read<HubController.ExtraSelection>(); return await extras.Prepare(user, selection.Kind, selection.ForeignId, selection.Edition, cancellation); }
        match = Regex.Match(route, "^Extras/Requests/([0-9a-f]{48})/(Confirm|Cancel|Receipt)$");
        if (match.Success) { var id = match.Groups[1].Value; return match.Groups[2].Value switch { "Confirm" when post => await extras.Confirm(user, id, Verify, cancellation), "Cancel" when post => extras.Cancel(user, id), "Receipt" when !post => extras.Receipt(user, id), _ => throw new HubError("Invalid request operation.") }; }
        match = Regex.Match(route, "^Me/ExtraWatchlist/(music|book)/([^/]+)$");
        if (match.Success && post) { var update = Read<HubController.ExternalWatchUpdate>(); var recorded = await extras.Follow(user, match.Groups[1].Value, match.Groups[2].Value, update.Subscribed, update.Revision, Verify, cancellation, update.SaveOnly); return personal.State(user) with { Activity = recorded }; }
        if (route == "Deletions") { if (!post) return queue.List(user); var selection = Read<HubController.Proposal>(); await queue.Propose(user, selection.RequestId, selection.Item, cancellation); return new { Saved = true }; }
        if (route == "Deletions/Refresh" && post) { await queue.Refresh(user, cancellation); return queue.List(user); }
        match = Regex.Match(route, "^Deletions/([0-9a-f]{48})/Undo$"); if (match.Success && post) { queue.Undo(user, match.Groups[1].Value); return new { Saved = true }; }
        if (route == "Me/Consent" && !post) return queue.Notices(user);
        match = Regex.Match(route, "^Me/Consent/([0-9a-f]{48})$"); if (match.Success && post) { await queue.Respond(user, match.Groups[1].Value, Read<HubController.ConsentResponse>().Approve, cancellation); return new { Saved = true }; }
        if (!route.StartsWith("Messaging/", StringComparison.Ordinal)) throw new HubError("This operation requires native Jellyfin. Activity cannot configure the server or impersonate a player.");
        var actor = await Service<OperatorAuthority>().Require(user.Id, cancellation); if (actor.Discord != context.Discord) throw new HubError("Operator identity changed.", "operator_denied"); var messages = Service<Messaging>();
        if (route == "Messaging/Access" && !post) return new { Operator = true };
        if (route == "Messaging/Destinations" && !post) return await messages.Destinations(actor, cancellation);
        if (route == "Messaging/Inbox" && !post) return await messages.Inbox(actor, Q("kind", "all"), Q("guild") is { Length: > 0 } guild ? guild : null, Q("peer") is { Length: > 0 } peer ? peer : null, Q("channel") is { Length: > 0 } channel ? channel : null, Q("query"), Page(), Q("oldest") == "true", cancellation);
        if (route == "Messaging/History" && !post) return await messages.History(actor, Page(), cancellation);
        if (route == "Messaging/Diagnostics" && !post) return await messages.Diagnostics(actor, cancellation);
        match = Regex.Match(route, "^Messaging/Inbox/([0-9]{1,20})$"); if (match.Success && !post) return await messages.Message(actor, match.Groups[1].Value, cancellation);
        if (route is "Messaging/Prepare" or "Messaging/Chat" && post)
        { var request = Read<MessageController.Compose>(); if (route == "Messaging/Chat" && (request.Mode != "normal" || request.Members)) throw new HubError("Broadcasts need an announcement preview."); var plan = await messages.Prepare(actor, request.Mode, request.Content, request.Targets, request.Uploads, request.RequestId, request.Members, cancellation); return route == "Messaging/Chat" ? await messages.Confirm(actor, plan.Id, cancellation) : plan; }
        match = Regex.Match(route, "^Messaging/Plans/([0-9a-f]{48})(?:/(Confirm|Cancel))?$");
        if (match.Success) { var id = match.Groups[1].Value; if (!post && match.Groups[2].Value.Length == 0) return await messages.Receipt(actor, id, cancellation); if (post && match.Groups[2].Value == "Confirm") return await messages.Confirm(actor, id, cancellation); if (!post || match.Groups[2].Value != "Cancel") throw new HubError("Invalid send operation."); await messages.Cancel(actor, id, cancellation); return new { Cancelled = true }; }
        if (route == "Messaging/Uploads" && post)
        { var form = await Request.ReadFormAsync(cancellation); var file = form.Files.GetFile("file") ?? throw new HubError("Select a file."); if (file.Length is 0 or > Messaging.FileLimit) throw new HubError("Uploads are limited to 8 MiB."); using var stream = new MemoryStream(); await file.CopyToAsync(stream, cancellation); return await messages.Upload(actor, file.FileName, stream.ToArray(), cancellation); }
        match = Regex.Match(route, "^Messaging/Inbox/([^/]+)/Assets/([^/]+)$");
        if (match.Success && !post)
        {
            MessageBlob blob; var id = match.Groups[1].Value; var key = match.Groups[2].Value;
            if (key.StartsWith("upload:", StringComparison.Ordinal)) blob = await messages.SentUpload(actor, id, key[7..], cancellation);
            else { var before = await messages.Asset(actor, id, key, cancellation); blob = await Service<MessageAssets>().Download(before.Url, before.Filename, cancellation); if (before != await messages.Asset(actor, id, key, cancellation)) throw new HubError("Attachment changed or was removed."); }
            Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'"; return Q("preview") == "true" && blob.Mime == "image/png" ? File(blob.Bytes, blob.Mime) : File(blob.Bytes, "application/octet-stream", blob.Filename);
        }
        throw new HubError("Unknown Activity operation.");
    });
}
