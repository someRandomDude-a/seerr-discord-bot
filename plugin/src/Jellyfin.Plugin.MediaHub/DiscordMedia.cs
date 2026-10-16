using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private static readonly HashSet<string> MediaCommands = ["dashboard", "search", "discover", "library", "requests", "watchlist", "deletions", "notifications", "link", "storage", "status", "seerr"];
    private MediaCatalog catalog => provider.GetRequiredService<MediaCatalog>();
    private PersonalService personal => provider.GetRequiredService<PersonalService>();
    private DiscordControls controls => provider.GetRequiredService<DiscordControls>();
    private static string Clip(string? text, int limit) => string.IsNullOrWhiteSpace(text) ? "—" : text.Length > limit ? text[..limit] + "…" : text;
    private async Task RegisterMedia(SocketGuild guild)
    {
        foreach (var name in MediaCommands)
        {
            var command = new SlashCommandBuilder().WithName(name).WithDescription(name switch
            { "dashboard" => "Your private media hub", "search" => "Find movies, series, music or books", "discover" => "Discover movies or series", "library" => "Your accessible media library", "requests" => "Your requests", "watchlist" => "Your saved titles", "deletions" => "Shared deletion queue and your consent inbox", "notifications" => "Your notification preferences", "storage" => "Verified service storage space", "status" => "Integration verification", "seerr" => "Open Seerr with your own account", _ => "Link your Jellyfin account" });
            if (name is "search" or "discover" or "library") command.AddOption("kind", ApplicationCommandOptionType.String, name == "discover" ? "movie or tv" : "all (library), movie, tv, music or book", name == "search");
            if (name is "search" or "library") command.AddOption("query", ApplicationCommandOptionType.String, "Search text", name == "search");
            if (name is "search" or "discover" or "library" or "requests") command.AddOption("page", ApplicationCommandOptionType.Integer, "Page number", false);
            if (name == "requests") command.AddOption("all", ApplicationCommandOptionType.Boolean, "All requests with Seerr permission", false);
            if (name == "notifications") command.AddOption("enabled", ApplicationCommandOptionType.Boolean, "Allow Discord updates, or mute all personal updates", false);
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
        if (name == "notifications" && bool.TryParse(Option("enabled"), out var enabled))
        {
            var state = personal.State(user); personal.Save(user, state.Revision, state.Preferences with { DiscordNotifications = enabled, Muted = !enabled });
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
        var view = HubStore.Nonce(); var builder = new ComponentBuilder(); var embed = new EmbedBuilder();
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
            for (var i = 0; i < Math.Min(20, page.Items.Count); i++)
            {
                var card = page.Items[i]; embed.AddField($"{i + 1}. {Clip(card.Title, 100)}", card.Status + (card.Is4k ? " · 4K" : ""));
                Button((i + 1).ToString(), new("details", card.Kind, card.ExternalId, card.NativeId));
            }
            if (page.Page > 1) Button("Previous", action with { Page = page.Page - 1 });
            if (page.Page < page.Pages) Button("Next", action with { Page = page.Page + 1 }); Home();
        }
        else if (action.Action is "details" or "prepare" or "confirm" or "cancel" or "open" or "follow")
        {
            MediaCard? card = null;
            if (action.Action == "cancel")
            {
                provider.GetRequiredService<MediaRequests>().Cancel(user, action.Token ?? ""); embed.WithTitle("Request plan cancelled"); Home();
            }
            else if (action.Action == "confirm")
            {
                var receipt = await provider.GetRequiredService<MediaRequests>().Confirm(user, action.Token ?? "", VerifyActor, cancellation);
                activityUpdate = receipt.Activity;
                embed.WithTitle(receipt.State == "completed" ? "Requested" : "Not confirmed").WithDescription(receipt.State == "completed" ? "Your request was saved." : "Check your Seerr requests before trying again."); Home();
            }
            else if (action.Action == "prepare")
            {
                var plan = await provider.GetRequiredService<MediaRequests>().Prepare(user, action.Kind, action.Id, action.All, null, cancellation);
                embed.WithTitle(Clip(plan.Title, 200)).WithDescription("Confirm request · " + (action.All ? "4K" : "Standard") + (action.Kind == "tv" ? " · All seasons" : ""));
                Button("Confirm request", new("confirm", Token: plan.Id), ButtonStyle.Success); Button("Cancel", new("cancel", Token: plan.Id)); Home();
            }
            else
            {
                if (action.Kind is "movie" or "tv" && action.Id > 0) card = await catalog.Details(user, action.Kind, action.Id, cancellation, action.Action == "details");
                else if (action.Native is Guid id)
                {
                    var native = provider.GetRequiredService<IViewerMedia>().Find(user, id) ?? throw new HubError("Item unavailable.");
                    if (action.Action == "details") activityUpdate = provider.GetRequiredService<MediaActivity>().Record(user, id, MediaInteraction.Details);
                    card = new(action.Kind, action.Id, id, native.Title, "", "Available", true, false, null);
                }
                if (card is null) throw new HubError("Item unavailable.");
                if (action.Action == "follow")
                {
                    await VerifyActor();
                    if (card.Kind is "movie" or "tv" && card.ExternalId > 0) activityUpdate = await provider.GetRequiredService<WatchlistSync>().Follow(user, new(card.Kind, card.ExternalId), true, action.Revision, VerifyActor, cancellation);
                    else activityUpdate = personal.Bell(user, card.NativeId ?? throw new HubError("Choose an accessible edition in Jellyfin."), true, action.Revision).Activity;
                }
                embed.WithTitle(Clip(card.Title, 200)).WithDescription((action.Action == "follow" ? "Saved and subscribed.\n" : "") + card.Status + (card.Activity?.JellyfinUpdated == false ? "\nActivity saved locally; Jellyfin timestamp update unavailable." : "") + "\n" + Clip(card.Overview, 1800));
                if (card.NativeId is Guid nativeId)
                {
                    Button("Open Jellyfin", action with { Action = "open" });
                    if (action.Action == "open")
                    {
                        activityUpdate = provider.GetRequiredService<MediaActivity>().Record(user, nativeId, MediaInteraction.Open);
                        if (settings.Current.PublicJellyfinUrl.Length > 0) builder.WithButton("Watch", style: ButtonStyle.Link, url: settings.Current.PublicJellyfinUrl.TrimEnd('/') + "/web/index.html#!/details?id=" + nativeId.ToString("N"), row: 1);
                    }
                }
                if (card.NativeId is not null || card.Kind is "movie" or "tv" && card.ExternalId > 0) Button("Subscribe", action with { Action = "follow", Revision = personal.State(user).Revision });
                if (card.Kind is "movie" or "tv" && card.ExternalId > 0)
                { Button("Request", action with { Action = "prepare", All = false }); Button("Request 4K", action with { Action = "prepare", All = true }); }
                Home();
            }
        }
        else if (action.Action == "watchlist")
        {
            var state = personal.State(user); embed.WithTitle("Your watchlist");
            var external = state.ExternalWatchlist ?? []; var extras = state.ExtraWatchlist ?? [];
            var all = external.Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", row.Kind, row.ExternalId, Revision: state.Revision)))
                .Concat(extras.Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", row.Kind, ForeignId: row.ForeignId, Revision: state.Revision))))
                .Concat(state.Watchlist.Where(row => !external.Any(e => e.NativeId == row.ItemId) && !extras.Any(e => e.NativeId == row.ItemId)).Select(row => (row.Title, row.Subscribed, Control: new MediaControl("watchOptions", Native: row.ItemId, Kind: row.Kind, Revision: state.Revision)))).ToArray();
            if (action.Page is < 1 or > 100) throw new HubError("Invalid watchlist page.");
            foreach (var row in all.Skip((action.Page - 1) * 18).Take(18)) { embed.AddField(Clip(row.Title, 100), row.Subscribed ? "Notifications on" : "Notifications off"); Button((count + 1).ToString(), row.Control); }
            if (action.Page > 1) Button("Previous", action with { Page = action.Page - 1 }); if (action.Page * 18 < all.Length) Button("Next", action with { Page = action.Page + 1 });
            if (embed.Fields.Count == 0) embed.WithDescription("No accessible saved titles."); Button("Sync with Seerr", new("sync")); Home();
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
        else if (action.Action is "deletions" or "undo" or "consent")
        {
            var queue = provider.GetRequiredService<DeletionQueue>();
            if (action.Action == "undo") queue.Undo(user, action.Token ?? "");
            if (action.Action == "consent") await queue.Respond(user, action.Token ?? "", action.All, cancellation);
            embed.WithTitle("Deletion queue");
            foreach (var notice in queue.Notices(user).Take(5))
            { embed.AddField(Clip(notice.Title, 100), "Allow deletion?"); Button("Agree " + (embed.Fields.Count), new("consent", Token: notice.Id, All: true), ButtonStyle.Success); Button("Decline " + embed.Fields.Count, new("consent", Token: notice.Id)); }
            foreach (var row in queue.List(user).Take(10))
            { embed.AddField(Clip(row.Title, 100), row.State + " · " + row.Owner + (row.DueAt is DateTimeOffset due ? " · <t:" + due.ToUnixTimeSeconds() + ":R>" : "")); if (row.CanUndo) Button("Undo " + embed.Fields.Count, new("undo", Token: row.Id)); }
            if (embed.Fields.Count == 0) embed.WithDescription("Nothing pending."); Home();
        }
        else if (action.Action == "notifications")
        {
            var p = personal.State(user).Preferences; embed.WithTitle("Your notifications").WithDescription(p.Muted ? "Muted" : $"Jellyfin: {(p.JellyfinNotifications ? "on" : "off")} · Discord: {(p.DiscordNotifications ? "on" : "off")}\nUse /notifications enabled:true to allow DMs or enabled:false to mute. More preferences are in Jellyfin Media Hub."); Home();
            foreach (var notice in provider.GetRequiredService<NotificationFeed>().List(user).Take(15)) embed.AddField(Clip(notice.Title, 100), notice.Status);
        }
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
        }
        else throw new HubError("Unknown control.");
        if (activityUpdate?.JellyfinUpdated == false) embed.WithDescription((embed.Description ?? "") + (activityUpdate.LocalRecorded ? "\nActivity saved locally; Jellyfin timestamp update unavailable." : "\nActivity update could not be verified."));
        await VerifyActor();
        // Modify the deferred original response, including component clicks: one private message, not a stream of duplicates.
        var message = await interaction.ModifyOriginalResponseAsync(m => { m.Content = ""; m.Embed = embed.Build(); m.Components = builder.Build(); m.AllowedMentions = AllowedMentions.None; },
            new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
        controls.Bind(view, message.Id);
    }
}
