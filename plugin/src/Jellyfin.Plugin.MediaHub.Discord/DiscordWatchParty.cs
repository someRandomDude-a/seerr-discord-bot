using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.MediaHub.DiscordIntegration;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private async Task RenderWatchParty(SocketInteraction interaction, User user, MediaControl action)
    {
        var commands = provider.GetRequiredService<DiscordPartyCommands>(); var subject = interaction.User.Id.ToString();
        var view = HubStore.Nonce(); var components = new ComponentBuilder(); var embed = new EmbedBuilder().WithTitle("Private SyncPlay watch party");
        void Control(string label, MediaControl payload) => components.WithButton(label, controls.Add(view, subject, user.Id, payload));
        WatchPartyLink? link = null;
        if (action.Action == "watchparty:create") link = (await commands.Create(subject, user.Id, action.Query, OperationToken)).Invite;
        else if (action.Action == "watchparty:invite" && action.Native is Guid party) link = await commands.Invite(subject, user.Id, party, OperationToken);
        else if (action.Action == "watchparty:revoke" && action.Native is Guid revokeParty && Guid.TryParse(action.Token, out var invite))
        {
            await commands.Revoke(subject, user.Id, revokeParty, invite, OperationToken);
            embed.WithDescription("Invitation revoked by Private SyncPlay. No replacement was generated.");
        }
        else if (action.Action != "watchparty:status") throw new HubError("Invalid watch-party action.");
        if (link is not null)
        {
            embed.WithDescription("Private SyncPlay generated this bearer invitation. Share deliberately: it can be forwarded, permits one signed-in redemption and expires by " + link.ExpiresAt.ToString("u") + ". Anonymous guests are disabled. Admission and playback remain with Private SyncPlay; this does not prove anyone joined or started playing.");
            components.WithButton("Open / copy provider invitation", style: ButtonStyle.Link, url: link.Url);
            Control("Revoke this invitation", new("watchparty:revoke", Native: link.PartyId, Token: link.InviteId.ToString()));
        }
        else if (action.Action == "watchparty:status")
        {
            var state = await commands.Current(subject, user.Id, OperationToken);
            if (!state.Active)
            {
                embed.WithDescription("Your linked native player is not in a party. Create a private party and provider-generated invitation? This requires that existing signed-in Jellyfin player.");
                Control("Create party and invitation", new("watchparty:create", Query: string.IsNullOrWhiteSpace(action.Query) ? "Watch party" : action.Query));
            }
            else
            {
                embed.WithDescription($"{state.ViewerCount} of {state.MaxViewers} viewers. " + (state.Owner ? "Generate an invitation for your current party?" : "Only the current party owner may generate an invitation. Open Private SyncPlay for playback and membership controls."));
                if (state.Owner) Control("Generate provider invitation", new("watchparty:invite", Native: state.PartyId));
            }
        }
        Control("Refresh party status", new("watchparty:status"));
        await interaction.ModifyOriginalResponseAsync(message => { message.Content = ""; message.Embed = embed.Build(); message.Components = components.Build(); message.AllowedMentions = AllowedMentions.None; });
        controls.Bind(view, (await interaction.GetOriginalResponseAsync()).Id);
    }
}
