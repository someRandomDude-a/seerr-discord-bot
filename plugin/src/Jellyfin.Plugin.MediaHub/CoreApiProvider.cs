using System.Text.Json;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Versioned in-process API. Integrations never receive the store, key, SQL access, or a host administrator identity.</summary>
public sealed class CoreApiProvider(IServiceProvider services)
{
    private T S<T>() where T : notnull => services.GetRequiredService<T>();
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static string Reply(object? value) => JsonSerializer.Serialize(new CoreReply(true, JsonSerializer.SerializeToElement(value)));
    private static string Failure(string message, string code) => JsonSerializer.Serialize(new CoreReply(false, JsonSerializer.SerializeToElement(new { }), message, code));
    internal async Task<string> Transport(string operation, object payload, CancellationToken token)
    {
        var callback = services.GetKeyedService<Func<string, string, CancellationToken, Task<string>>>(CoreApi.DiscordTransportKey)
            ?? throw new HubError("The separate Discord Integration plugin is unavailable.");
        return await callback(operation, JsonSerializer.Serialize(payload), token);
    }
    public async Task<string> Invoke(string subject, string operation, string encoded, CancellationToken cancellation)
    {
        try
        {
            var maximum = operation == "messages.upload" ? 12 * 1024 * 1024 : operation is "transport.receive" or "transport.edit" ? 1024 * 1024 : 65536;
            if (subject.Length > 32 || operation.Length > 100 || encoded.Length > maximum) throw new HubError("Integration input exceeds its supported budget.");
            SafeJson.Validate(System.Text.Encoding.UTF8.GetBytes(encoded), operation is "messages.upload" ? 12 * 1024 * 1024 : 1024 * 1024, operation is "messages.upload" ? 12 * 1024 * 1024 : 131072);
            var request = JsonSerializer.Deserialize<CoreRequest>(encoded, Strict) ?? throw new HubError("Invalid integration request.");
            var p = request.Payload;
            T Read<T>(string name) => p.GetProperty(name).Deserialize<T>(Strict) ?? throw new HubError("Incomplete integration request.");
            string Text(string name) => Read<string>(name);
            int Number(string name) => Read<int>(name);
            bool Flag(string name) => Read<bool>(name);
            Guid Item() => Read<Guid>("Item");
            if (operation == "transport.settings")
            {
                var s = S<SettingsService>().Current;
                return Reply(new { Revision = s.RuntimeRevision, s.DiscordEnabled, s.AllowedGuilds, s.PublicJellyfinUrl, Features = new { s.Features.MessageContent, s.Features.MembersIntent, s.Features.MaxRecipients, s.Features.DiscordApplicationId, s.Features.InboxChannels, s.Features.ActivityEnabled, s.Features.OAuthPkce } });
            }
            if (operation == "transport.token") { if (!S<SettingsService>().Current.DiscordEnabled) throw new HubError("Discord is disabled."); return Reply(S<SettingsService>().DiscordToken()); }
            if (operation == "transport.webUrl") return Reply(S<SettingsService>().WebUrl());
            if (operation == "transport.oauthConfiguration")
            {
                var settings = S<SettingsService>(); var current = settings.Current;
                return Reply(new { ApplicationId = current.Features.DiscordApplicationId, ClientSecret = settings.Unprotect(current.Features.DiscordClientSecretEncrypted), current.Features.ActivityEnabled, current.AllowedGuilds });
            }
            if (operation == "transport.legacyDiscordConfiguration")
            {
                var settings = S<SettingsService>(); var s = settings.Retained;
                return Reply(new Jellyfin.Plugin.WatchlistDeletion.Api.Contracts.DiscordConfigurationSnapshot(s.Revision, s.DiscordEnabled,
                    settings.Unprotect(s.DiscordTokenEncrypted), s.AllowedGuilds, s.Features.DiscordApplicationId, settings.Unprotect(s.Features.DiscordClientSecretEncrypted),
                    s.Features.ActivityEnabled, s.Features.OAuthPkce, s.Features.MessageContent, s.Features.MembersIntent, s.Features.Operators, s.Features.SeerrOperators,
                    s.Features.InboxChannels, s.Features.MessageRetentionDays, s.Features.MaxRecipients));
            }
            if (operation == "transport.confirmDiscordMigration") { S<SettingsService>().Protection.MarkDiscordConfigurationMigrated(); return Reply(true); }
            if (operation == "transport.discordMigrationState") return Reply(S<SettingsService>().Protection.DiscordConfigurationMigrated());
            if (operation == "identity.profile")
            {
                if (!ulong.TryParse(subject, out var snowflake) || snowflake == 0) throw new HubError("Invalid external identity.");
                if (!S<HubStore>().HasDiscordLink(subject)) return Reply(new { Linked = false, JellyfinUrl = S<SettingsService>().Current.PublicJellyfinUrl });
                var actor = await S<HostIdentity>().Discord(subject); var state = S<PersonalService>().State(actor);
                if ((await S<HostIdentity>().Discord(subject)).Id != actor.Id) throw new HubError("Native identity changed.");
                return Reply(new { Linked = true, UserId = actor.Id, Name = actor.Username, JellyfinUrl = S<SettingsService>().Current.PublicJellyfinUrl, state.Preferences.Theme });
            }
            if (operation == "parties.review") return Reply(S<SettingsService>().Authenticate("MediaHub.PartyManagement.v1", Text("Value")));
            if (operation == "transport.beginLink")
            {
                if (!ulong.TryParse(subject, out var snowflake) || snowflake == 0) throw new HubError("Invalid external identity.");
                return Reply(S<HubStore>().BeginLink(subject));
            }
            if (operation == "transport.handoff") return Reply(S<LinkFlow>().HandoffUrl(Text("Code")));
            if (operation == "transport.receive") { S<Messaging>().Receive(Read<MessageView>("Message"), Read<System.Text.Json.Nodes.JsonNode>("Rich"), Flag("Direct"), Flag("Mentioned"), Flag("IsReply")); return Reply(true); }
            if (operation == "transport.edit") { S<Messaging>().Edit(Text("Id"), Text("Channel"), Text("Content"), Read<System.Text.Json.Nodes.JsonNode>("Rich")); return Reply(true); }
            if (operation == "transport.delete") { S<Messaging>().Delete(Text("Id"), Text("Channel")); return Reply(true); }
            if (operation == "transport.download") return Reply(await S<MessageAssets>().Download(Text("Url"), Text("Filename"), cancellation));
            if (operation == "delivery.messages") { await S<Messaging>().Deliver(cancellation); return Reply(true); }
            if (operation == "delivery.consent") { var callback = Text("Callback"); await S<ConsentDelivery>().Deliver(async (discord, notice, ct) => { await Transport("callback.consent", new { Callback = callback, Discord = discord, Notice = notice }, ct); }, cancellation); return Reply(true); }
            if (operation == "delivery.notifications") { var callback = Text("Callback"); await S<NotificationFeed>().Deliver(async (discord, notice, ct) => { await Transport("callback.notification", new { Callback = callback, Discord = discord, Notice = notice }, ct); }, cancellation); return Reply(true); }

            // Subject is authenticated by the integration's transport. The core resolves the
            // retained, native-approved binding again for EVERY operation, including reads.
            // ExpectedUser detects relinking between rendering and action; it is never authority.
            var session = await S<HostIdentity>().DiscordSession(subject); var user = S<HostIdentity>().Active(session.UserId);
            if (operation == "identity.resolve") return Reply(new { User = user.Id, Session = session.Id, Device = session.DeviceId });
            if (request.ExpectedUser != user.Id) throw new HubError("Linked identity changed. Reopen the action.");
            async Task Verify() { if ((await S<HostIdentity>().DiscordSession(subject)).UserId != user.Id) throw new HubError("Linked identity changed."); }
            PersonalService personal() => S<PersonalService>();
            MediaCatalog catalog() => S<MediaCatalog>();
            DeletionQueue queue() => S<DeletionQueue>();
            WatchlistSync sync() => S<WatchlistSync>();
            object? result;
            switch (operation)
            {
                case "personal.state": result = personal().State(user); break;
                case "personal.save": result = personal().Save(user, Read<long>("Revision"), Read<PersonalPreferences>("Preferences")); break;
                case "personal.add": result = personal().Add(user, Item(), Read<long>("Revision")); break;
                case "personal.bell": result = personal().Bell(user, Item(), Flag("Subscribed"), Read<long>("Revision")); break;
                case "personal.remove": result = personal().Remove(user, Item(), Read<long>("Revision")); break;
                case "media.find": result = S<IViewerMedia>().Find(user, Item()) ?? throw new HubError("Item unavailable."); break;
                case "media.activity": result = S<MediaActivity>().Record(user, Item(), Read<MediaInteraction>("Action")); break;
                case "catalog.library": result = catalog().Library(user, Text("Kind"), Text("Query"), Number("Page")); break;
                case "catalog.browse": result = await catalog().Browse(user, Text("Kind"), Text("Query"), Number("Page"), cancellation); break;
                case "catalog.requests": result = await catalog().Requests(user, Flag("All"), Number("Page"), cancellation); break;
                case "catalog.details": result = await catalog().Details(user, Text("Kind"), Number("Id"), cancellation, Flag("Interact")); break;
                case "catalog.native": result = catalog().NativeDetails(user, Item(), Flag("Interact")); break;
                case "catalog.editions": result = catalog().Editions(user, Text("Kind"), Number("Id")); break;
                case "catalog.openEdition": result = catalog().OpenEdition(user, Text("Kind"), Number("Id"), Item()); break;
                case "catalog.open": result = await catalog().Open(user, Text("Kind"), Number("Id"), cancellation); break;
                case "catalog.poster":
                    var card = await catalog().Details(user, Text("Kind"), Number("Id"), cancellation);
                    var poster = await S<PosterAssets>().Download(card.Poster ?? "", cancellation);
                    await Verify(); if ((await catalog().Details(user, Text("Kind"), Number("Id"), cancellation)).Poster != card.Poster) throw new HubError("Artwork changed.");
                    result = new { poster.Bytes, poster.Mime, Filename = "poster" }; break;
                case "catalog.permissions": result = await catalog().Permissions(user, cancellation); break;
                case "requests.prepare": result = await S<MediaRequests>().Prepare(user, Text("Kind"), Number("Id"), Flag("Is4k"), p.GetProperty("Seasons").Deserialize<int[]>(), cancellation); break;
                case "requests.confirm": result = await S<MediaRequests>().Confirm(user, Text("Id"), Verify, cancellation); break;
                case "requests.receipt": result = S<MediaRequests>().Receipt(user, Text("Id")); break;
                case "requests.cancel": result = S<MediaRequests>().Cancel(user, Text("Id")); break;
                case "extras.search": result = await S<ExtraMedia>().Search(user, Text("Kind"), Text("Query"), Number("Page"), cancellation); break;
                case "extras.details": result = await S<ExtraMedia>().Details(user, Text("Kind"), Text("Foreign"), Flag("Interact"), cancellation); break;
                case "extras.prepare": result = await S<ExtraMedia>().Prepare(user, Text("Kind"), Text("Foreign"), p.GetProperty("Edition").Deserialize<string>(), cancellation); break;
                case "extras.confirm": result = await S<ExtraMedia>().Confirm(user, Text("Id"), Verify, cancellation); break;
                case "extras.cancel": result = S<ExtraMedia>().Cancel(user, Text("Id")); break;
                case "extras.receipt": result = S<ExtraMedia>().Receipt(user, Text("Id")); break;
                case "extras.follow": result = await S<ExtraMedia>().Follow(user, Text("Kind"), Text("Foreign"), p.GetProperty("Subscribed").Deserialize<bool?>(), Read<long>("Revision"), Verify, cancellation, Flag("SaveOnly")); break;
                case "watch.follow": result = await sync().Follow(user, Read<MediaKey>("Key"), p.GetProperty("Subscribed").Deserialize<bool?>(), Read<long>("Revision"), Verify, cancellation, Flag("SaveOnly")); break;
                case "watch.sync": result = await sync().Sync(user, Verify, cancellation); break;
                case "watch.issues": result = sync().Issues(user); break;
                case "watch.review": result = await sync().Review(user, Read<MediaKey>("Key"), Verify, cancellation); break;
                case "watch.accept": result = await sync().AcceptRemote(user, Text("Token"), Verify, cancellation); break;
                case "watch.legacyReview": result = await sync().ReviewLegacy(user, Verify, cancellation); break;
                case "watch.legacyAccept": await sync().AcceptLegacy(user, Text("Token"), Verify, cancellation); result = true; break;
                case "deletions.owned": result = await S<IRequestAuthority>().FindOwned(user, Item(), cancellation); break;
                case "deletions.permission": result = new { Scope = new DeletionPermissions(S<SettingsService>()).Scope(user).ToString(), NativeTargetVerificationRequired = true }; break;
                case "deletions.proposeNative": await queue().Propose(user, 0, Item(), cancellation); result = true; break;
                case "deletions.list": result = queue().List(user); break;
                case "deletions.refresh": await queue().Refresh(user, cancellation); result = true; break;
                case "deletions.status": result = queue().Status(user, Text("Id")); break;
                case "deletions.notices": result = queue().Notices(user); break;
                case "deletions.propose": await queue().Propose(user, Number("Request"), Item(), cancellation); result = true; break;
                case "deletions.respond": await queue().Respond(user, Text("Id"), Flag("Approve"), cancellation); result = true; break;
                case "deletions.undo": queue().Undo(user, Text("Id")); result = true; break;
                case "notifications.list": result = await S<NotificationFeed>().ListAsync(user, cancellation); break;
                case "notifications.seen": S<NotificationFeed>().Seen(user, Text("Id")); result = true; break;
                case "notifications.diagnostics": result = S<NotificationFeed>().Diagnostics(user); break;
                case "players.destinations": result = S<NativePlayback>().Destinations(session); break;
                case "players.history": result = S<NativePlayback>().History(session); break;
                case "players.prepare": result = S<NativePlayback>().Prepare(session, Text("Target"), Item()); break;
                case "players.confirm": result = await S<NativePlayback>().Confirm(session, Text("Id"), Verify, cancellation); break;
                case "players.cancel": result = S<NativePlayback>().Cancel(session, Text("Id")); break;
                case "players.acknowledge": result = S<NativePlayback>().Acknowledge(session, Text("Id")); break;
                case "players.receipt": result = S<NativePlayback>().Receipt(session, Text("Id")); break;
                case "seerr.viewer": using (var scope = await S<SeerrBridge>().Open(user, cancellation)) result = scope.Viewer; break;
                case "storage": using (var storageScope = await S<SeerrBridge>().Open(user, cancellation)) { result = await S<ArrRegistry>().Storage(cancellation); await storageScope.Verify(cancellation); } break;
                case "controls.add": result = S<DiscordControls>().Add(Text("View"), subject, user.Id, Read<MediaControl>("Control")); break;
                case "controls.bind": S<DiscordControls>().Bind(Text("View"), Read<ulong>("Message")); result = true; break;
                case "controls.claim": result = S<DiscordControls>().Claim(Text("Id"), subject, user.Id, Read<ulong>("Message")); break;
                default: result = await OperatorOperation(operation, user.Id, p, cancellation); break;
            }
            await Verify(); return Reply(result);
        }
        catch (HubError error) { return Failure(error.Message, error.Code ?? "core_rejected"); }
        catch { return Failure("Operation not confirmed. Inspect current state before retrying.", "core_uncertain"); }
    }
    private async Task<object?> OperatorOperation(string operation, Guid user, JsonElement p, CancellationToken token)
    {
        var actor = await S<OperatorAuthority>().Require(user, token); var messages = S<Messaging>();
        string T(string key) => p.GetProperty(key).GetString() ?? throw new HubError("Incomplete operation.");
        string? N(string key) => p.GetProperty(key).Deserialize<string>();
        switch (operation)
        {
            case "operator.require": return actor;
            case "messages.destinations": return await messages.Destinations(actor, token);
            case "messages.upload": return await messages.Upload(actor, T("Filename"), p.GetProperty("Bytes").Deserialize<byte[]>()!, token);
            case "messages.prepare": return await messages.Prepare(actor, T("Mode"), T("Content"), p.GetProperty("Targets").Deserialize<MessageTarget[]>()!, p.GetProperty("Uploads").Deserialize<string[]>()!, N("Request"), p.GetProperty("Members").GetBoolean(), token);
            case "messages.preview": return await messages.GetPreview(actor, T("Id"), token);
            case "messages.confirm": return await messages.Confirm(actor, T("Id"), token);
            case "messages.cancel": await messages.Cancel(actor, T("Id"), token); return true;
            case "messages.receipt": return await messages.Receipt(actor, T("Id"), token);
            case "messages.history": return await messages.History(actor, p.GetProperty("Page").GetInt32(), token);
            case "messages.inbox": return await messages.Inbox(actor, T("Kind"), N("Guild"), N("Peer"), N("Channel"), T("Query"), p.GetProperty("Page").GetInt32(), p.GetProperty("Oldest").GetBoolean(), token);
            case "messages.message": return await messages.Message(actor, T("Id"), token);
            case "messages.diagnostics": return await messages.Diagnostics(actor, token);
            case "messages.uploadRead": return await messages.DownloadUpload(actor, T("Id"), token);
            case "messages.sentUpload": return await messages.SentUpload(actor, T("Id"), T("File"), token);
            case "messages.asset": var asset = await messages.Asset(actor, T("Id"), T("Key"), token); return new { asset.Url, asset.Filename, asset.Revision };
            default: throw new HubError("Unsupported integration operation.");
        }
    }
}
