using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private static readonly HashSet<string> MediaCommands = ["dashboard", "search", "discover", "library", "requests", "watchlist", "deletions", "notifications", "link", "storage", "status", "seerr", "quota", "players"];
    private MediaCatalog catalog => provider.GetRequiredService<MediaCatalog>();
    private PersonalService personal => provider.GetRequiredService<PersonalService>();
    private DiscordControls controls => provider.GetRequiredService<DiscordControls>();
    private static string Clip(string? text, int limit) => string.IsNullOrWhiteSpace(text) ? "—" : text.Length > limit ? text[..limit] + "…" : text;
    private async Task RegisterMedia(SocketGuild guild)
    {
        foreach (var name in MediaCommands)
        {
            var command = new SlashCommandBuilder().WithName(name).WithDescription(name switch
            { "dashboard" => "Your private media hub", "search" => "Find movies, series, music or books", "discover" => "Discover movies or series", "library" => "Your accessible media library", "requests" => "Your requests", "watchlist" => "Your saved titles", "deletions" => "Shared deletion queue and your consent inbox", "notifications" => "Your notification preferences", "storage" => "Verified service storage space", "status" => "Integration verification", "seerr" => "Open Seerr with your own account", "quota" => "Your Seerr permissions and request quotas", "players" => "Your real native player command receipts", _ => "Link your Jellyfin account" });
            if (name is "search" or "discover" or "library") command.AddOption("kind", ApplicationCommandOptionType.String, name == "discover" ? "movie or tv" : "all (library), movie, tv, music or book", name == "search");
            if (name is "search" or "library") command.AddOption("query", ApplicationCommandOptionType.String, "Search text", name == "search");
            if (name is "search" or "discover" or "library" or "requests") command.AddOption("page", ApplicationCommandOptionType.Integer, "Page number", false);
            if (name == "requests") command.AddOption("all", ApplicationCommandOptionType.Boolean, "All requests with Seerr permission", false);
            if (name == "notifications") {
                command.AddOption("enabled", ApplicationCommandOptionType.Boolean, "Allow Discord updates, or mute all personal updates", false);
                command.AddOption("jellyfin", ApplicationCommandOptionType.Boolean, "Show Jellyfin notification updates", false);
                command.AddOption("discord", ApplicationCommandOptionType.Boolean, "Send Discord notification updates", false);
                command.AddOption("muted", ApplicationCommandOptionType.Boolean, "Mute personal status updates", false);
                command.AddOption("own_requests", ApplicationCommandOptionType.Boolean, "Notify on your own requests", false);
                command.AddOption("removal_delay", ApplicationCommandOptionType.Integer, "Your minimum removal delay in days; server minimum applies", false);
            }
            await guild.CreateApplicationCommandAsync(command.Build(), new RequestOptions { CancelToken = OperationToken, Timeout = 15000, RetryMode = RetryMode.AlwaysFail });
        }
    }
    private async Task MediaSlash(SocketSlashCommand interaction)
    {
        if (interaction.CommandName == "link")
        {
            var code = store.BeginLink(interaction.User.Id.ToString());
            await interaction.ModifyOriginalResponseAsync(m => { m.Content = $"Open Media Hub in Jellyfin. Approve only a link/code you requested.\nYour Discord ID: `{interaction.User.Id}` · Fallback code: `{code}` · Expires in 5 minutes."; m.AllowedMentions = AllowedMentions.None;
                if (settings.Current.Features.DiscordApplicationId.Length > 0) m.Components = new ComponentBuilder().WithButton("Link in Jellyfin", style: ButtonStyle.Link, url: provider.GetRequiredService<LinkFlow>().HandoffUrl(code)).Build(); }); return;
        }
        var user = await identity.Discord(interaction.User.Id.ToString());
        string Option(string key, string fallback = "") => interaction.Data.Options.FirstOrDefault(o => o.Name == key)?.Value?.ToString() ?? fallback;
        var name = interaction.CommandName;
        var input = new MediaControl(name, Option("kind", name == "library" ? "all" : "movie"), Page: int.TryParse(Option("page"), out var page) ? page : 1, Query: Option("query"), All: Option("all") == "True");
        if (name == "players") input = new("playerHistory");
        if (name == "notifications" && interaction.Data.Options.Any())
        {
            var state = personal.State(user); var p = state.Preferences;
            if (bool.TryParse(Option("enabled"), out var enabled)) p = p with { DiscordNotifications = enabled, Muted = !enabled };
            bool Flag(string key, bool old) => bool.TryParse(Option(key), out var value) ? value : old;
            personal.Save(user, state.Revision, p with { JellyfinNotifications = Flag("jellyfin", p.JellyfinNotifications), DiscordNotifications = Flag("discord", p.DiscordNotifications), Muted = Flag("muted", p.Muted), OwnRequestNotifications = Flag("own_requests", p.OwnRequestNotifications), DeletionDelayDays = int.TryParse(Option("removal_delay"), out var days) ? days : p.DeletionDelayDays });
        }
        await RenderMedia(interaction, user, input);
    }
    private async Task MediaClick(SocketMessageComponent interaction)
    {
        if (interaction.GuildId is not ulong guild || !settings.Current.AllowedGuilds.Contains(guild.ToString())) throw new HubError("This server is no longer allowed.");
        if (interaction.Message.Flags?.HasFlag(MessageFlags.Ephemeral) != true) throw new HubError("Use your private command response.");
        var user = await identity.Discord(interaction.User.Id.ToString());
        var action = controls.Claim(interaction.Data.CustomId["mediahub:control:".Length..], interaction.User.Id.ToString(), user.Id, interaction.Message.Id);
        await RenderMedia(interaction, user, action);
    }
    private async Task RenderMedia(SocketInteraction interaction, User user, MediaControl action)
    {
        if (action.Action.StartsWith("operator:", StringComparison.Ordinal)) { await RenderOperator(interaction, user, action); return; }
        if (action.Action.StartsWith("extra:", StringComparison.Ordinal) || action.Action == "search" && action.Kind is "music" or "book") { await RenderExtra(interaction, user, action); return; }
        var view = HubStore.Nonce(); var builder = new ComponentBuilder(); var embed = new EmbedBuilder(); var gallery = new List<Embed>();
        var count = 0;
        void Button(string label, MediaControl payload, ButtonStyle style = ButtonStyle.Secondary)
        {
            if (count >= 25) return;
            builder.WithButton(label, controls.Add(view, interaction.User.Id.ToString(), user.Id, payload), style, row: count++ / 5);
        }
        void Home() => Button("Home", new("dashboard"));
        var cancellation = OperationToken;
        ActivityReceipt? activityUpdate = null;
        async Task VerifyActor()
        { if ((await identity.Discord(interaction.User.Id.ToString())).Id != user.Id) throw new HubError("Your linked account changed. Reopen the command."); }
        if (action.Action is "watchBell" or "watchRemove")
        {
            await VerifyActor();
            if (action.ForeignId is not null) activityUpdate = await provider.GetRequiredService<ExtraMedia>().Follow(user, action.Kind, action.ForeignId, action.Action == "watchRemove" ? null : action.All, action.Revision, VerifyActor, cancellation);
            else if (action.Id > 0) activityUpdate = await provider.GetRequiredService<WatchlistSync>().Follow(user, new(action.Kind, action.Id), action.Action == "watchRemove" ? null : action.All, action.Revision, VerifyActor, cancellation);
            else if (action.Native is Guid item)
            {
                if (action.Action == "watchRemove") personal.Remove(user, item, action.Revision);
                else activityUpdate = personal.Bell(user, item, action.All, action.Revision).Activity;
            }
            action = new("watchlist");
        }
        if (action.Action is "search" or "discover" or "library" or "requests")
        {
            var page = action.Action == "library" ? catalog.Library(user, action.Kind, action.Query, action.Page)
                : action.Action == "requests" ? await catalog.Requests(user, action.All, action.Page, cancellation)
                : await catalog.Browse(user, action.Kind, action.Query, action.Page, cancellation);
            embed.WithTitle(action.Action == "requests" ? action.All ? "All requests" : "Your requests" : action.Action == "library" ? "Your library" : "Discover");
            embed.WithDescription(page.Items.Count == 0 ? "No results." : $"Page {page.Page} of {page.Pages}");
            if (action.Offset is not (0 or 9 or 18)) throw new HubError("Invalid gallery offset.");
            foreach (var pair in page.Items.Skip(action.Offset).Take(9).Select((card, index) => (Card: card, Index: action.Offset + index)))
            {
                var card = pair.Card; var title = new EmbedBuilder().WithTitle($"{pair.Index + 1}. {Clip(card.Title, 100)}").WithDescription(Clip(card.Status + (card.Is4k ? " · 4K" : "") + "\n" + card.Overview, 220));
                if (DiscordPoster(card.Poster) is string poster) title.WithThumbnailUrl(poster); gallery.Add(title.Build());
                Button((pair.Index + 1).ToString(), new("details", card.Kind, card.ExternalId, card.NativeId, NativeOnly: card.NativeOnly));
            }
            if (action.Offset > 0) Button("Earlier titles", action with { Offset = action.Offset - 9 });
            if (action.Offset + 9 < page.Items.Count) Button("More titles", action with { Offset = action.Offset + 9 });
            if (page.Page > 1) Button("Previous page", action with { Page = page.Page - 1, Offset = 0 });
            if (page.Page < page.Pages) Button("Next page", action with { Page = page.Page + 1, Offset = 0 }); Home();
        }
        else if (action.Action is "players" or "playerPrepare" or "playerConfirm" or "playerReceipt" or "playerCancel" or "playerHistory" or "playerReview" or "playerAcknowledge")
        {
            var players = provider.GetRequiredService<NativePlayback>(); var actor = await identity.DiscordSession(interaction.User.Id.ToString());
            if (actor.UserId != user.Id) throw new HubError("Linked player changed.");
            if (action.Action == "players")
            {
                var destinations = players.Destinations(actor); embed.WithTitle("Your real native players").WithDescription("Only active players signed in as you are listed. Selecting a device prepares a command; it does not start playback.");
                if (action.Page is < 1 or > 100) throw new HubError("Invalid player page.");
                foreach (var destination in destinations.Skip((action.Page - 1) * 18).Take(18)) Button(Clip(destination.Name + " · " + destination.Client, 75), action with { Action = "playerPrepare", Token = destination.Id });
                if (action.Page > 1) Button("Earlier players", action with { Page = action.Page - 1 }); if (action.Page * 18 < destinations.Count) Button("More players", action with { Page = action.Page + 1 });
                if (destinations.Count == 0) embed.WithDescription("Open your real Jellyfin player first; none currently supports media control.");
            }
            else if (action.Action == "playerHistory")
            {
                var history = players.History(actor); if (action.Page is < 1 or > 100) throw new HubError("Invalid player receipt page."); embed.WithTitle("Your native player receipts").WithDescription("Command acceptance is not proof of playback. Inspect the real player before acknowledging uncertain outcomes.");
                foreach (var row in history.Skip((action.Page - 1) * 15).Take(15)) { embed.AddField(Clip(row.Preview.Title + " · " + row.Preview.Device, 150), row.Receipt.State); Button("Receipt " + embed.Fields.Count, new("playerReceipt", Token: row.Receipt.Id)); }
                if (action.Page > 1) Button("Earlier receipts", action with { Page = action.Page - 1 }); if (action.Page * 15 < history.Count) Button("More receipts", action with { Page = action.Page + 1 });
            }
            else if (action.Action == "playerReview")
            {
                _ = players.Receipt(actor, action.Token ?? ""); embed.WithTitle("Inspect your real player first").WithDescription("Acknowledgement sends no command and does not prove playback. A later command could duplicate an earlier effect. Have you inspected the actual player?");
                Button("I inspected it · acknowledge, no retry", action with { Action = "playerAcknowledge" });
            }
            else if (action.Action == "playerPrepare")
            {
                var plan = players.Prepare(actor, action.Token ?? "", action.Native ?? Guid.Empty); embed.WithTitle(Clip(plan.Title, 200)).WithDescription("Play now on " + plan.Device + "? This may replace its current playback. Preview expires in two minutes.");
                Button("Confirm native play command", new("playerConfirm", Token: plan.Id), ButtonStyle.Success); Button("Cancel unsent preview", new("playerCancel", Token: plan.Id));
            }
            else
            {
                var receipt = action.Action == "playerConfirm" ? await players.Confirm(actor, action.Token ?? "", VerifyActor, cancellation) : action.Action == "playerCancel" ? players.Cancel(actor, action.Token ?? "") : action.Action == "playerAcknowledge" ? players.Acknowledge(actor, action.Token ?? "") : players.Receipt(actor, action.Token ?? "");
                embed.WithTitle("Native command · " + receipt.State).WithDescription("Acceptance is not confirmed playback. A receipt refresh never resends. Inspect uncertain outcomes in the native Media Hub page before creating a replacement.");
                Button("Refresh player receipt", new("playerReceipt", Token: receipt.Id));
                if (receipt.State is "sending" or "uncertain") Button("Review uncertain outcome", new("playerReview", Token: receipt.Id));
                if (receipt.State == "prepared") Button("Cancel unsent preview", new("playerCancel", Token: receipt.Id));
            } Button("Player history", new("playerHistory")); Home();
        }
        else if (action.Action is "editions" or "openEdition")
        {
            if (action.Action == "openEdition")
            {
                var opened = catalog.OpenEdition(user, action.Kind, action.Id, action.Native ?? Guid.Empty); activityUpdate = opened.Activity;
                embed.WithTitle("Open selected Jellyfin edition").WithDescription("Open in your own signed-in player. This does not start or confirm playback.");
                if (settings.Current.PublicJellyfinUrl.Length > 0) builder.WithButton("Open this edition", style: ButtonStyle.Link, url: settings.Current.PublicJellyfinUrl.TrimEnd('/') + "/web/index.html#/details?id=" + opened.Item, row: count++ / 5);
            }
            else
            {
                var editions = catalog.Editions(user, action.Kind, action.Id); if (action.Page is < 1 or > 4) throw new HubError("Invalid edition page.");
                embed.WithTitle("Choose an accessible Jellyfin edition"); foreach (var edition in editions.Skip((action.Page - 1) * 15).Take(15)) Button(Clip(edition.Title, 65), action with { Action = "openEdition", Native = edition.Id });
                if (action.Page > 1) Button("Earlier editions", action with { Page = action.Page - 1 }); if (action.Page * 15 < editions.Count) Button("More editions", action with { Page = action.Page + 1 });
                if (editions.Count == 0) embed.WithDescription("No currently accessible native edition.");
            } Home();
        }
        else if (action.Action == "seasons")
        {
            var card = await catalog.Details(user, "tv", action.Id, cancellation); var seasons = card.Seasons ?? [];
            if (action.Page is < 1 or > 100 || action.Seasons?.Length > 100 || action.Seasons?.Any(s => !seasons.Contains(s)) == true) throw new HubError("Season selection changed. Reopen the title.");
            var chosen = action.Seasons ?? []; embed.WithTitle(Clip(card.Title, 200)).WithDescription("Choose seasons · " + (action.All ? "4K" : "Standard") + "\nSelected: " + (chosen.Length == 0 ? "All seasons" : string.Join(", ", chosen)));
            foreach (var season in seasons.Skip((action.Page - 1) * 15).Take(15))
                Button((chosen.Contains(season) ? "✓ " : "") + "Season " + season, action with { Seasons = chosen.Contains(season) ? chosen.Except([season]).ToArray() : chosen.Append(season).Order().ToArray() });
            if (action.Page > 1) Button("Earlier seasons", action with { Page = action.Page - 1 });
            if (action.Page * 15 < seasons.Count) Button("More seasons", action with { Page = action.Page + 1 });
            Button("Review request", action with { Action = "prepare" }); Button("Clear selection · all seasons", action with { Seasons = null }); Home();
        }
        else if (action.Action is "details" or "prepare" or "confirm" or "cancel" or "open" or "follow" or "saveWatch" or "receipt")
        {
            MediaCard? card = null;
            if (action.Action == "cancel")
            {
                provider.GetRequiredService<MediaRequests>().Cancel(user, action.Token ?? ""); embed.WithTitle("Request plan cancelled"); Home();
            }
            else if (action.Action is "confirm" or "receipt")
            {
                var receipt = action.Action == "confirm" ? await provider.GetRequiredService<MediaRequests>().Confirm(user, action.Token ?? "", VerifyActor, cancellation) : provider.GetRequiredService<MediaRequests>().Receipt(user, action.Token ?? "");
                activityUpdate = receipt.Activity;
                embed.WithTitle(receipt.State == "completed" ? "Requested" : "Not confirmed").WithDescription(receipt.State == "completed" ? "Your request was saved." : "Check your Seerr requests before trying again. Refresh only reads the receipt; it never resends."); Button("Refresh receipt", new("receipt", Token: action.Token)); Home();
            }
            else if (action.Action == "prepare")
            {
                var plan = await provider.GetRequiredService<MediaRequests>().Prepare(user, action.Kind, action.Id, action.All, action.Seasons, cancellation);
                embed.WithTitle(Clip(plan.Title, 200)).WithDescription("Confirm request · " + (action.All ? "4K" : "Standard") + (action.Kind == "tv" ? action.Seasons is { Length: > 0 } ? " · Seasons " + string.Join(", ", action.Seasons) : " · All seasons" : ""));
                Button("Confirm request", new("confirm", Token: plan.Id), ButtonStyle.Success); Button("Cancel", new("cancel", Token: plan.Id)); Home();
            }
            else
            {
                if (action.NativeOnly && action.Native is Guid nativeItemId) card = catalog.NativeDetails(user, nativeItemId, action.Action == "details");
                else if (action.Kind is "movie" or "tv" && action.Id > 0) card = await catalog.Details(user, action.Kind, action.Id, cancellation, action.Action == "details");
                else if (action.Native is Guid id)
                {
                    var native = provider.GetRequiredService<IViewerMedia>().Find(user, id) ?? throw new HubError("Item unavailable.");
                    if (action.Action == "details") activityUpdate = provider.GetRequiredService<MediaActivity>().Record(user, id, MediaInteraction.Details);
                    card = new(action.Kind, action.Id, id, native.Title, "", "Available", true, false, null);
                }
                if (card is null) throw new HubError("Item unavailable.");
                if (action.Action is "follow" or "saveWatch")
                {
                    await VerifyActor();
                    if (!card.NativeOnly && (card.Kind is "movie" or "tv") && card.ExternalId > 0) activityUpdate = await provider.GetRequiredService<WatchlistSync>().Follow(user, new(card.Kind, card.ExternalId), action.Action == "follow", action.Revision, VerifyActor, cancellation, action.Action == "saveWatch");
                    else activityUpdate = (action.Action == "saveWatch" ? personal.Add(user, card.NativeId ?? throw new HubError("Choose an accessible edition in Jellyfin."), action.Revision) : personal.Bell(user, card.NativeId ?? throw new HubError("Choose an accessible edition in Jellyfin."), true, action.Revision)).Activity;
                }
                embed.WithTitle(Clip(card.Title, 200)).WithDescription((action.Action == "follow" ? "Saved and subscribed.\n" : "") + card.Status + (card.Activity?.JellyfinUpdated == false ? "\nActivity saved locally; Jellyfin timestamp update unavailable." : "") + "\n" + Clip(card.Overview, 1800));
                if (DiscordPoster(card.Poster) is string poster) embed.WithThumbnailUrl(poster);
                if (card.NativeId is Guid nativeId)
                {
                    Button("Choose your player", new("players", Native: nativeId));
                    Button("Open Jellyfin", action with { Action = "open" });
                    if (action.Action == "open")
                    {
                        activityUpdate = provider.GetRequiredService<MediaActivity>().Record(user, nativeId, MediaInteraction.Open);
                        if (settings.Current.PublicJellyfinUrl.Length > 0) builder.WithButton("Watch", style: ButtonStyle.Link, url: settings.Current.PublicJellyfinUrl.TrimEnd('/') + "/web/index.html#/details?id=" + nativeId.ToString("N"), row: 1);
                    }
                }
                if (card.NativeId is not null || card.Kind is "movie" or "tv" && card.ExternalId > 0) { Button("Add to watchlist", action with { Action = "saveWatch", Revision = personal.State(user).Revision }); Button("Subscribe", action with { Action = "follow", Revision = personal.State(user).Revision }); }
                if (card.NativeId is Guid removalItem && card.Kind is "movie" or "tv" or "Movie" or "Series") Button("Propose media removal", new("removalChoices", Native: removalItem));
                if (card.Kind is "movie" or "tv" && card.ExternalId > 0)
                { if (card.NativeId is null) Button("Choose Jellyfin edition", action with { Action = "editions", Page = 1 }); Button("Request", action with { Action = card.Kind == "tv" ? "seasons" : "prepare", All = false, Page = 1, Seasons = null }); Button("Request 4K", action with { Action = card.Kind == "tv" ? "seasons" : "prepare", All = true, Page = 1, Seasons = null }); }
                Home();
            }
        }
        else if (action.Action == "watchlist")
        {
            var state = personal.State(user); embed.WithTitle("Your watchlist");
            var external = state.ExternalWatchlist ?? []; var extras = state.ExtraWatchlist ?? [];
            var all = external.Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", row.Kind, row.ExternalId, Revision: state.Revision)))
                .Concat(extras.Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", row.Kind, ForeignId: row.ForeignId, Revision: state.Revision))))
                .Concat(state.Watchlist.Where(row => !external.Any(e => e.NativeId == row.ItemId || e.NativeIds?.Contains(row.ItemId) == true) && !extras.Any(e => e.NativeId == row.ItemId)).Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", Native: row.ItemId, Kind: row.Kind, Revision: state.Revision)))).ToArray();
            if (action.Page is < 1 or > 100) throw new HubError("Invalid watchlist page.");
            foreach (var row in all.Skip((action.Page - 1) * 18).Take(18)) { embed.AddField(Clip(row.Title, 100), row.Subscribed ? "Notifications on" : "Notifications off"); Button((count + 1).ToString(), row.Control); }
            if (action.Page > 1) Button("Previous", action with { Page = action.Page - 1 }); if (action.Page * 18 < all.Length) Button("Next", action with { Page = action.Page + 1 });
            if (embed.Fields.Count == 0) embed.WithDescription("No accessible saved titles."); Button("Sync with Seerr", new("sync")); Button("Review pending sync", new("syncIssues")); if (state.WatchlistReviewRequired) Button("Review retained watchlist", new("legacyWatchReview")); Home();
        }
        else if (action.Action == "watchOptions")
        {
            var state = personal.State(user);
            var row = action.Id > 0 ? state.ExternalWatchlist?.FirstOrDefault(w => w.Kind == action.Kind && w.ExternalId == action.Id) : null;
            var extra = action.ForeignId is not null ? state.ExtraWatchlist?.FirstOrDefault(w => w.Kind == action.Kind && w.ForeignId == action.ForeignId) : null;
            var native = action.Id <= 0 && action.ForeignId is null ? state.Watchlist.FirstOrDefault(w => w.ItemId == action.Native) : null;
            if (row is null && native is null && extra is null || state.Revision != action.Revision) throw new HubError("Your watchlist changed. Reopen it.");
            var subscribed = row?.Subscribed ?? extra?.Subscribed ?? native!.Subscribed;
            embed.WithTitle(Clip(row?.Title ?? extra?.Title ?? native!.Title, 200)).WithDescription(subscribed ? "Notifications on" : "Notifications off");
            Button(subscribed ? "Unsubscribe" : "Subscribe", action with { Action = "watchBell", All = !subscribed });
            Button("Remove from watchlist", action with { Action = "watchRemove" }); Button("Watchlist", new("watchlist"));
        }
        else if (action.Action == "sync")
        {
            var result = await provider.GetRequiredService<WatchlistSync>().Sync(user, VerifyActor, cancellation);
            embed.WithTitle(result.Complete ? "Synced" : "Sync pending").WithDescription(result.Complete ? "Your movie/series watchlist is up to date." : "An earlier change is unconfirmed. No automatic resend was made."); Button("Watchlist", new("watchlist")); Home();
        }
        else if (action.Action is "syncIssues" or "syncReview" or "syncAccept" or "legacyWatchReview" or "legacyWatchAccept")
        {
            var sync = provider.GetRequiredService<WatchlistSync>();
            if (action.Action == "legacyWatchReview") { var review = await sync.ReviewLegacy(user, VerifyActor, cancellation); embed.WithTitle("Review retained watchlist").WithDescription(Clip(string.Join('\n', review.Titles), 2500) + "\nApproval permits a later explicit union sync. No membership change or uncertain retry is sent now."); Button("Approve retained list · no write", new("legacyWatchAccept", Token: review.Token)); }
            else if (action.Action == "legacyWatchAccept") { await sync.AcceptLegacy(user, action.Token ?? "", VerifyActor, cancellation); embed.WithTitle("Retained list approved").WithDescription("No upstream write sent. Explicit sync is now available."); }
            else if (action.Action == "syncAccept") { await sync.AcceptRemote(user, action.Token ?? "", VerifyActor, cancellation); embed.WithTitle("Observed membership adopted locally").WithDescription("No retry was sent to Seerr."); }
            else if (action.Action == "syncReview") { var review = await sync.Review(user, new(action.Kind, action.Id), VerifyActor, cancellation); embed.WithTitle(Clip(review.Title, 200)).WithDescription("Currently " + (review.RemoteMembership ? "saved" : "not saved") + " in Seerr. Accepting changes only the local list and clears this pending change. No retry is sent. Expires in five minutes."); Button("Accept observed Seerr state locally", new("syncAccept", Token: review.Token)); }
            else { var issues = sync.Issues(user); if (action.Page is < 1 or > 100) throw new HubError("Invalid sync issue page."); embed.WithTitle("Pending watchlist changes"); foreach (var issue in issues.Skip((action.Page - 1) * 15).Take(15)) { embed.AddField(Clip(issue.Title, 100), issue.State + " · intended " + (issue.Desired ? "saved" : "removed")); if (issue.State != "ready") Button("Inspect " + embed.Fields.Count, new("syncReview", issue.Kind, issue.ExternalId)); } if (issues.Count == 0) embed.WithDescription("Nothing pending."); if (action.Page > 1) Button("Earlier changes", action with { Page = action.Page - 1 }); if (action.Page * 15 < issues.Count) Button("More changes", action with { Page = action.Page + 1 }); }
            Button("Watchlist", new("watchlist")); Home();
        }
        else if (action.Action is "removalChoices" or "removalConfirm" or "removalPropose")
        {
            var item = action.Native ?? throw new HubError("Choose an accessible movie or show.");
            var owned = await provider.GetRequiredService<IRequestAuthority>().FindOwned(user, item, cancellation);
            if (action.Action == "removalChoices") { embed.WithTitle("Choose your own request to remove").WithDescription("Only the original requester may propose removal. All recent viewers must consent and the full delay applies."); foreach (var request in owned.Take(18)) Button("Review " + (request.Is4k ? "4K" : "standard") + " request " + request.RequestId, new("removalConfirm", Id: request.RequestId, Native: item)); }
            else { if (!owned.Any(row => row.RequestId == action.Id)) throw new HubError("Owned request is no longer available."); if (action.Action == "removalConfirm") { embed.WithTitle("Confirm media removal proposal").WithDescription("This starts a consent proposal, not immediate removal. Every current recent viewer must agree; new activity invalidates prior consent. The server’s full delay applies."); Button("Confirm removal proposal", action with { Action = "removalPropose" }, ButtonStyle.Danger); } else { await VerifyActor(); await provider.GetRequiredService<DeletionQueue>().Propose(user, action.Id, item, cancellation); embed.WithTitle("Removal proposed").WithDescription("Consent and delay checks remain in force."); Button("Removal queue", new("deletions")); } } Home();
        }
        else if (action.Action is "deletions" or "undo" or "consent")
        {
            var queue = provider.GetRequiredService<DeletionQueue>();
            if (action.Action == "undo") queue.Undo(user, action.Token ?? "");
            if (action.Action == "consent") await queue.Respond(user, action.Token ?? "", action.All, cancellation);
            embed.WithTitle("Deletion queue");
            var entries = queue.Notices(user).Select(n => (Notice: (ConsentNotice?)n, Queue: (QueueItem?)null)).Concat(queue.List(user).Select(q => (Notice: (ConsentNotice?)null, Queue: (QueueItem?)q))).ToArray();
            if (action.Page is < 1 or > 100) throw new HubError("Invalid removal queue page.");
            foreach (var entry in entries.Skip((action.Page - 1) * 10).Take(10))
            {
                if (entry.Notice is { } notice) { embed.AddField(Clip(notice.Title, 100), "Allow deletion? No keeps removal blocked while your activity remains recent."); Button("Yes " + embed.Fields.Count, new("consent", Token: notice.Id, All: true), ButtonStyle.Success); Button("No " + embed.Fields.Count, new("consent", Token: notice.Id)); }
                else if (entry.Queue is { } row) { embed.AddField(Clip(row.Title, 100), row.State + " · " + row.Owner + (row.DueAt is DateTimeOffset due ? " · <t:" + due.ToUnixTimeSeconds() + ":R>" : "")); if (row.CanUndo) Button("Undo " + embed.Fields.Count, new("undo", Token: row.Id)); }
            }
            if (action.Page > 1) Button("Previous removal page", new("deletions", Page: action.Page - 1)); if (action.Page * 10 < entries.Length) Button("Next removal page", new("deletions", Page: action.Page + 1));
            if (embed.Fields.Count == 0) embed.WithDescription("Nothing pending."); Home();
        }
        else if (action.Action is "notifications" or "notificationSeen")
        {
            if (action.Action == "notificationSeen") provider.GetRequiredService<NotificationFeed>().Seen(user, action.Token ?? "");
            var state = personal.State(user); var p = state.Preferences; embed.WithTitle("Your notifications").WithDescription($"Jellyfin: {(p.JellyfinNotifications ? "on" : "off")} · Discord: {(p.DiscordNotifications ? "on" : "off")} · {(p.Muted ? "muted" : "not muted")} · Own requests: {(p.OwnRequestNotifications ? "on" : "off")} · Minimum removal delay: {state.EffectiveDeletionDelayDays} days.\nUse /notifications to set each preference independently."); Home(); Button("Delivery diagnostics", new("notificationDiagnostics")); Button("Consent requests", new("deletions"));
            var notices = await provider.GetRequiredService<NotificationFeed>().ListAsync(user, cancellation); if (action.Page is < 1 or > 10) throw new HubError("Invalid notification page.");
            foreach (var notice in notices.Skip((action.Page - 1) * 15).Take(15)) { embed.AddField(Clip(notice.Title, 100), notice.Status); if (!notice.Seen) Button("Mark read " + embed.Fields.Count, new("notificationSeen", Token: notice.Id, Page: action.Page)); }
            if (action.Page > 1) Button("Earlier updates", new("notifications", Page: action.Page - 1)); if (action.Page * 15 < notices.Count) Button("More updates", new("notifications", Page: action.Page + 1));
        }
        else if (action.Action == "notificationDiagnostics") { var d = provider.GetRequiredService<NotificationFeed>().Diagnostics(user); embed.WithTitle("Your notification diagnostics").WithDescription("Last completed scan: " + (d.LastCompletedScan?.ToString("u") ?? "none in this process") + "\n" + string.Join('\n', d.Deliveries.Select(pair => pair.Key + ": " + pair.Value)) + "\nUncertain deliveries are never automatically resent."); Button("Notifications", new("notifications")); Home(); }
        else if (action.Action == "quota") { var p = await catalog.Permissions(user, cancellation); embed.WithTitle("Your Seerr permissions and quotas"); string Quota(UserQuota q) => $"Used {q.Used?.ToString() ?? "unknown"} · remaining {q.Remaining?.ToString() ?? "unknown"} · limit {q.Limit?.ToString() ?? "unknown"} / {q.Days?.ToString() ?? "unknown"} days · restricted {q.Restricted?.ToString() ?? "unknown"}"; embed.AddField("Movies · " + p.Movies + " · 4K " + p.Movie4k, Quota(p.MovieQuota)); embed.AddField("Series · " + p.Series + " · 4K " + p.Series4k, Quota(p.SeriesQuota)); Home(); }
        else if (action.Action is "storage" or "status")
        {
            using var scope = await provider.GetRequiredService<SeerrBridge>().Open(user, cancellation);
            var spaces = await provider.GetRequiredService<ArrRegistry>().Storage(cancellation); await scope.Verify(cancellation);
            embed.WithTitle(action.Action == "storage" ? "Storage" : "Integration verification").WithDescription("Verified just now · plugin preview features remain incomplete.");
            foreach (var space in spaces.Take(20)) embed.AddField(space.Service + " #" + space.Instance, space.Verified ? $"{space.Free / 1073741824d:F1} / {space.Total / 1073741824d:F1} GiB free" : "Not verified"); Home();
        }
        else if (action.Action == "seerr")
        {
            using var scope = await provider.GetRequiredService<SeerrBridge>().Open(user, cancellation); var url = new Uri(scope.Viewer.Origin);
            if (url.Scheme != "https") throw new HubError("Open Seerr using your administrator's trusted address. External buttons require HTTPS.");
            embed.WithTitle("Seerr").WithDescription("Sign in with your own account."); builder.WithButton("Open Seerr", style: ButtonStyle.Link, url: url.ToString()); count++; Home();
        }
        else if (action.Action == "dashboard")
        {
            embed.WithTitle("Media Hub").WithDescription("Browse your library, discover titles and manage your requests.");
            foreach (var tab in new[] { ("Library", "library"), ("Discover", "discover"), ("Requests", "requests"), ("Watchlist", "watchlist"), ("Deletion queue", "deletions"), ("Notifications", "notifications") }) Button(tab.Item1, new(tab.Item2, tab.Item2 == "library" ? "all" : "movie"));
            Button("Request quotas", new("quota")); Button("Player receipts", new("playerHistory"));
            if (settings.Current.PublicJellyfinUrl.Length > 0) builder.WithButton("Watch together · native Jellyfin player", style: ButtonStyle.Link, url: settings.WebUrl(), row: count++ / 5);
        }
        else throw new HubError("Unknown control.");
        if (activityUpdate?.JellyfinUpdated == false) embed.WithDescription((embed.Description ?? "") + (activityUpdate.LocalRecorded ? "\nActivity saved locally; Jellyfin timestamp update unavailable." : "\nActivity update could not be verified."));
        await VerifyActor();
        // Modify the deferred original response, including component clicks: one private message, not a stream of duplicates.
        var message = await interaction.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embeds = new[] { embed.Build() }.Concat(gallery).ToArray(); m.Components = builder.Build(); m.AllowedMentions = AllowedMentions.None; },
            new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
        controls.Bind(view, message.Id);
    }
    internal static string? DiscordPoster(string? path) => path is not null && System.Text.RegularExpressions.Regex.IsMatch(path, "\\A/[A-Za-z0-9_-]{1,100}\\.(jpg|png|webp)\\z") ? "https://image.tmdb.org/t/p/w185" + path : null;
}
