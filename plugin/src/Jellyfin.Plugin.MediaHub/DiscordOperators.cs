using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private static readonly HashSet<string> OperatorCommands = ["chat", "announce", "inbox", "servers", "deliveries"];
    private async Task RegisterOperatorCommands(SocketGuild guild)
    {
        foreach (var name in OperatorCommands)
        {
            var command = new SlashCommandBuilder().WithName(name).WithDescription("Operators: " + name);
            if (name is "chat" or "announce")
            {
                command.AddOption("message", ApplicationCommandOptionType.String, "Message text", false).AddOption("recipient", ApplicationCommandOptionType.User, "One human member of this server", false)
                    .AddOption("channel", ApplicationCommandOptionType.Channel, "One server channel", false).AddOption("attachment", ApplicationCommandOptionType.Attachment, "Up to 8 MiB", false);
                if (name == "announce") command.AddOption("members", ApplicationCommandOptionType.Boolean, "Also DM all human members; explicit preview required", false);
            }
            else
            {
                command.AddOption("page", ApplicationCommandOptionType.Integer, "Page number", false);
                if (name == "inbox") command.AddOption("query", ApplicationCommandOptionType.String, "Private inbox search", false);
                if (name == "deliveries") command.AddOption("job", ApplicationCommandOptionType.String, "One of your send IDs", false);
            }
            await guild.CreateApplicationCommandAsync(command.Build(), new RequestOptions { CancelToken = OperationToken, Timeout = 15000, RetryMode = RetryMode.AlwaysFail });
        }
    }
    private async Task OperatorSlash(SocketSlashCommand interaction)
    {
        var user = await identity.Discord(interaction.User.Id.ToString()); var actor = await provider.GetRequiredService<OperatorAuthority>().Require(user.Id, OperationToken);
        var messages = provider.GetRequiredService<Messaging>(); object? Value(string name) => interaction.Data.Options.FirstOrDefault(o => o.Name == name)?.Value;
        if (interaction.CommandName is "chat" or "announce")
        {
            var recipient = Value("recipient") as IUser; var channel = Value("channel") as IChannel;
            if (recipient is not null && channel is not null) throw new HubError("Choose one channel or one recipient.");
            var files = new List<string>();
            if (Value("attachment") is IAttachment attachment)
            {
                if (attachment.Size > Messaging.FileLimit) throw new HubError("Attachment exceeds 8 MiB.");
                var blob = await provider.GetRequiredService<MessageAssets>().Download(attachment.Url, attachment.Filename, OperationToken);
                files.Add((await messages.Upload(actor, blob.Filename, blob.Bytes, OperationToken)).Id);
            }
            var target = recipient is null ? new MessageTarget("channel", (channel?.Id ?? interaction.ChannelId ?? 0).ToString(), interaction.GuildId?.ToString()) : new MessageTarget("user", recipient.Id.ToString(), interaction.GuildId?.ToString());
            var mode = interaction.CommandName == "chat" ? "normal" : "announcement";
            var plan = await messages.Prepare(actor, mode, Value("message")?.ToString() ?? "", [target], files.ToArray(), "discord-" + interaction.Id, Value("members") is true, OperationToken);
            if (mode == "normal") { await messages.Confirm(actor, plan.Id, OperationToken); await RenderOperator(interaction, user, new("operator:receipt", Token: plan.Id)); }
            else await RenderOperator(interaction, user, new("operator:preview", Token: plan.Id));
            return;
        }
        await RenderOperator(interaction, user, new("operator:" + interaction.CommandName, Page: int.TryParse(Value("page")?.ToString(), out var page) ? page : 1, Query: Value("query")?.ToString() ?? "", Token: Value("job")?.ToString()));
    }
    private async Task RenderOperator(SocketInteraction interaction, User user, MediaControl action)
    {
        var authority = provider.GetRequiredService<OperatorAuthority>(); var actor = await authority.Require(user.Id, OperationToken);
        if (actor.Discord != interaction.User.Id.ToString()) throw new HubError("Operator identity changed.");
        var messages = provider.GetRequiredService<Messaging>(); var embed = new EmbedBuilder(); var builder = new ComponentBuilder(); var view = HubStore.Nonce(); var count = 0;
        void Button(string label, MediaControl payload) { if (count < 25) builder.WithButton(label, controls.Add(view, actor.Discord, actor.User, payload), row: count++ / 5); }
        if (action.Action is "operator:confirm" or "operator:cancel")
        { if (action.Action == "operator:confirm") await messages.Confirm(actor, action.Token ?? "", OperationToken); else await messages.Cancel(actor, action.Token ?? "", OperationToken); action = action with { Action = "operator:receipt" }; }
        if (action.Action == "operator:preview")
        {
            var plan = await messages.GetPreview(actor, action.Token ?? "", OperationToken); embed.WithTitle("Confirm announcement").WithDescription(Clip(plan.Content, 2000));
            embed.AddField("Destinations", Clip(string.Join('\n', plan.Destinations.Select(d => d.Label)), 1000));
            if (plan.Files.Count > 0) embed.AddField("Attachments", Clip(string.Join('\n', plan.Files.Select(f => f.Filename)), 1000));
            Button("Confirm announcement", action with { Action = "operator:confirm" }); Button("Cancel", action with { Action = "operator:cancel" });
        }
        else if (action.Action == "operator:receipt" || action.Action == "operator:deliveries" && action.Token is not null)
        {
            var job = await messages.Receipt(actor, action.Token ?? "", OperationToken); embed.WithTitle("Deliveries").WithDescription(Clip(job.Content, 1200));
            foreach (var delivery in job.Deliveries.Take(20)) embed.AddField(Clip(delivery.Destination, 100), delivery.State == "uncertain" ? "Not confirmed · check Discord before a new send" : delivery.State);
        }
        else if (action.Action == "operator:deliveries")
        {
            var jobs = await messages.History(actor, action.Page, OperationToken); embed.WithTitle("Your deliveries");
            foreach (var job in jobs) { embed.AddField(job.Mode + " · " + job.Created.ToString("u"), Clip(job.Content, 160)); Button((count + 1).ToString(), new("operator:receipt", Token: job.Id)); }
            if (jobs.Count == 20) Button("Next", action with { Page = action.Page + 1 });
            if (action.Page > 1) Button("Previous", action with { Page = action.Page - 1 });
        }
        else if (action.Action == "operator:servers")
        {
            if (action.Page is < 1 or > 100) throw new HubError("Invalid destination page."); var list = await messages.Destinations(actor, OperationToken); embed.WithTitle("Destinations");
            foreach (var d in list.Skip((action.Page - 1) * 20).Take(20)) embed.AddField(Clip(d.Label, 150), "Channel " + d.Target.Id);
            if (action.Page * 20 < list.Count) Button("Next", action with { Page = action.Page + 1 }); if (action.Page > 1) Button("Previous", action with { Page = action.Page - 1 });
        }
        else
        {
            var inbox = await messages.Inbox(actor, "all", null, null, null, action.Query, action.Page, false, OperationToken); embed.WithTitle("Private inbox");
            foreach (var message in inbox.Messages.Take(20)) embed.AddField(Clip(message.AuthorName + " · " + (message.GuildName ?? "DM"), 100), message.Deleted ? "Message deleted" : Clip(message.Content, 150));
            if (inbox.HasNext) Button("Next", action with { Page = action.Page + 1 }); if (action.Page > 1) Button("Previous", action with { Page = action.Page - 1 });
        }
        Button("Inbox", new("operator:inbox")); Button("Deliveries", new("operator:deliveries")); Button("Home", new("dashboard")); await authority.Check(actor, OperationToken);
        var sent = await interaction.ModifyOriginalResponseAsync(p => { p.Content = ""; p.Embed = embed.Build(); p.Components = builder.Build(); p.AllowedMentions = AllowedMentions.None; }, new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 }); controls.Bind(view, sent.Id);
    }
}
