using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private async Task RenderExtra(SocketInteraction interaction, User user, MediaControl action)
    {
        var extras = provider.GetRequiredService<ExtraMedia>(); var view = HubStore.Nonce(); var embed = new EmbedBuilder(); var components = new ComponentBuilder(); var count = 0;
        void Button(string title, MediaControl payload) { if (count < 25) components.WithButton(title, controls.Add(view, interaction.User.Id.ToString(), user.Id, payload), row: count++ / 5); }
        async Task Verify() { if ((await identity.Discord(interaction.User.Id.ToString())).Id != user.Id) throw new HubError("Linked account changed."); }
        var token = OperationToken; ActivityReceipt? recorded = null;
        if (action.Action == "search")
        {
            var page = await extras.Search(user, action.Kind, action.Query, action.Page, token); embed.WithTitle("Discover").WithDescription(page.Items.Count == 0 ? "No results." : $"Page {page.Page} of {page.Pages}");
            foreach (var card in page.Items) { embed.AddField(Clip(card.Title, 100), card.Status); Button((count + 1).ToString(), new("extra:details", card.Kind, ForeignId: card.ForeignId)); }
            if (page.Page > 1) Button("Previous", action with { Page = page.Page - 1 }); if (page.Page < page.Pages) Button("Next", action with { Page = page.Page + 1 });
        }
        else if (action.Action is "extra:confirm" or "extra:cancel" or "extra:receipt")
        {
            var receipt = action.Action == "extra:cancel" ? extras.Cancel(user, action.Token ?? "") : action.Action == "extra:receipt" ? extras.Receipt(user, action.Token ?? "") : await extras.Confirm(user, action.Token ?? "", Verify, token); recorded = receipt.Activity;
            embed.WithTitle(receipt.State == "completed" ? "Requested" : receipt.State == "cancelled" ? "Request plan cancelled" : "Not confirmed").WithDescription(receipt.State == "uncertain" ? "Check the media service before trying again." : "");
            Button("Refresh receipt", new("extra:receipt", Token: action.Token));
        }
        else if (action.Action == "extra:prepare")
        {
            var plan = await extras.Prepare(user, action.Kind, action.ForeignId ?? "", action.Edition, token); embed.WithTitle(Clip(plan.Title, 200)).WithDescription("Confirm selected item" + (action.Edition is null ? "" : " · Edition " + action.Edition));
            Button("Confirm request", new("extra:confirm", Token: plan.Id)); Button("Cancel", new("extra:cancel", Token: plan.Id));
        }
        else
        {
            var card = await extras.Details(user, action.Kind, action.ForeignId ?? "", action.Action == "extra:details", token); recorded = card.Activity;
            if (action.Action == "extra:follow") recorded = await extras.Follow(user, action.Kind, card.ForeignId, true, action.Revision, Verify, token);
            if (action.Action == "extra:saveWatch") recorded = await extras.Follow(user, action.Kind, card.ForeignId, false, action.Revision, Verify, token, saveOnly: true);
            embed.WithTitle(Clip(card.Title, 200)).WithDescription((action.Action == "extra:follow" ? "Saved and subscribed.\n" : "") + card.Status + "\n" + Clip(card.Overview, 1500));
            Button("Subscribe", action with { Action = "extra:follow", Revision = personal.State(user).Revision });
            Button("Add to watchlist", action with { Action = "extra:saveWatch", Revision = personal.State(user).Revision });
            if (card.Requestable)
            {
                if (card.Kind == "book")
                {
                    if (action.Page is < 1 or > 100) throw new HubError("Invalid edition page.");
                    foreach (var edition in card.Editions.Skip((action.Page - 1) * 18).Take(18)) Button("Request edition " + Clip(edition, 40), action with { Action = "extra:prepare", Edition = edition });
                    if (action.Page > 1) Button("Earlier editions", action with { Action = "extra:editions", Page = action.Page - 1 });
                    if (action.Page * 18 < card.Editions.Count) Button("More editions", action with { Action = "extra:editions", Page = action.Page + 1 });
                }
                else Button("Request album", action with { Action = "extra:prepare" });
            }
        }
        if (recorded?.JellyfinUpdated == false) embed.WithDescription((embed.Description ?? "") + (recorded.LocalRecorded ? "\nActivity saved locally; Jellyfin timestamp update unavailable." : "\nActivity update could not be verified."));
        Button("Home", new("dashboard")); await Verify();
        var message = await interaction.ModifyOriginalResponseAsync(p => { p.Content = ""; p.Embed = embed.Build(); p.Components = components.Build(); p.AllowedMentions = AllowedMentions.None; }, new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 }); controls.Bind(view, message.Id);
    }
}
