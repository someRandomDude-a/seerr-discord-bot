using Discord;
using Discord.WebSocket;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private static readonly HashSet<string> OperatorCommands = ["chat", "announce", "inbox", "servers", "deliveries", "messaging_status"];
    private async Task RegisterOperatorCommands(SocketGuild guild)
    {
        foreach (var name in OperatorCommands)
        {
            var command = new SlashCommandBuilder().WithName(name).WithDescription("Operators: " + name);
            if (name is "chat" or "announce")
            {
                command.AddOption("message", ApplicationCommandOptionType.String, "Message text", false).AddOption("recipient", ApplicationCommandOptionType.User, "One human member of this server", false)
                    .AddOption("channel", ApplicationCommandOptionType.Channel, "One server channel", false).AddOption("attachment", ApplicationCommandOptionType.Attachment, "Up to 8 MiB", false);
                foreach (var index in new[] { 2, 3, 4 }) command.AddOption("attachment" + index, ApplicationCommandOptionType.Attachment, "Extra file; total upload budget 16 MiB", false);
                if (name == "announce") foreach (var index in new[] { 2, 3, 4 }) command.AddOption("channel" + index, ApplicationCommandOptionType.Channel, "Additional announcement destination", false);
                if (name == "announce") command.AddOption("members", ApplicationCommandOptionType.Boolean, "Also DM all human members; explicit preview required", false);
            }
            else
            {
                command.AddOption("page", ApplicationCommandOptionType.Integer, "Page number", false);
                if (name == "inbox") {
                    command.AddOption("query", ApplicationCommandOptionType.String, "Private inbox search", false);
                    command.AddOption("kind", ApplicationCommandOptionType.String, "all, dm or guild", false);
                    command.AddOption("guild", ApplicationCommandOptionType.String, "Filter by accessible Discord server ID", false);
                    command.AddOption("channel", ApplicationCommandOptionType.String, "Filter by accessible channel ID", false);
                    command.AddOption("peer", ApplicationCommandOptionType.String, "Filter by conversation user ID", false);
                    command.AddOption("oldest", ApplicationCommandOptionType.Boolean, "Oldest first", false);
                    command.AddOption("reference", ApplicationCommandOptionType.String, "Open a retained message ID", false);
                }
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
            var files = new List<string>(); long uploaded = 0;
            foreach (var key in new[] { "attachment", "attachment2", "attachment3", "attachment4" }) if (Value(key) is IAttachment attachment)
            {
                uploaded += attachment.Size;
                if (attachment.Size > Messaging.FileLimit || uploaded > 2L * Messaging.FileLimit) throw new HubError("Attachments exceed the 8 MiB per-file / 16 MiB total budget.");
                var blob = await provider.GetRequiredService<MessageAssets>().Download(attachment.Url, attachment.Filename, OperationToken);
                files.Add((await messages.Upload(actor, blob.Filename, blob.Bytes, OperationToken)).Id);
            }
            var target = recipient is null ? new MessageTarget("channel", (channel?.Id ?? interaction.ChannelId ?? 0).ToString(), interaction.GuildId?.ToString()) : new MessageTarget("user", recipient.Id.ToString(), interaction.GuildId?.ToString());
            var targets = new List<MessageTarget> { target };
            foreach (var key in new[] { "channel2", "channel3", "channel4" }) if (Value(key) is IChannel extra) { if (recipient is not null) throw new HubError("Choose channels or one recipient, not both."); targets.Add(new("channel", extra.Id.ToString(), interaction.GuildId?.ToString())); }
            var mode = interaction.CommandName == "chat" ? "normal" : "announcement";
            var plan = await messages.Prepare(actor, mode, Value("message")?.ToString() ?? "", targets.ToArray(), files.ToArray(), "discord-" + interaction.Id, Value("members") is true, OperationToken);
            if (mode == "normal") { await messages.Confirm(actor, plan.Id, OperationToken); await RenderOperator(interaction, user, new("operator:receipt", Token: plan.Id)); }
            else await RenderOperator(interaction, user, new("operator:preview", Token: plan.Id));
            return;
        }
        await RenderOperator(interaction, user, new(Value("reference") is not null ? "operator:message" : "operator:" + interaction.CommandName, Kind: Value("kind")?.ToString() ?? "all", Page: int.TryParse(Value("page")?.ToString(), out var page) ? page : 1, Query: Value("query")?.ToString() ?? "", Token: (Value("reference") ?? Value("job"))?.ToString(), Guild: Value("guild")?.ToString(), Peer: Value("peer")?.ToString(), Channel: Value("channel")?.ToString(), Oldest: Value("oldest") is true));
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
        else if (action.Action == "operator:messaging_status") {
            var diagnostics = await messages.Diagnostics(actor, OperationToken); embed.WithTitle("Your operator delivery diagnostics").WithDescription(string.Join('\n', diagnostics.Select(pair => pair.Key + ": " + pair.Value)) + "\nUncertain sends are never automatically repeated.");
        }
        else if (action.Action is "operator:message" or "operator:asset")
        {
            var message = await messages.Message(actor, action.Token ?? "", OperationToken); embed.WithTitle(Clip(message.AuthorName + " · " + (message.GuildName ?? "DM"), 200)).WithDescription(message.Deleted ? "Message deleted" : Clip(message.Content, 2000));
            if (!message.Deleted)
            {
                if (action.Action == "operator:asset") {
                    MessageBlob blob;
                    var key = action.Edition ?? "";
                    if (key.StartsWith("upload:", StringComparison.Ordinal)) blob = await messages.SentUpload(actor, message.Id, key[7..], OperationToken);
                    else { var before = await messages.Asset(actor, message.Id, key, OperationToken); blob = await provider.GetRequiredService<MessageAssets>().Download(before.Url, before.Filename, OperationToken); if (before != await messages.Asset(actor, message.Id, key, OperationToken)) throw new HubError("Attachment changed or was removed."); }
                    await authority.Check(actor, OperationToken);
                    using var stream = new MemoryStream(blob.Bytes, false);
                    await interaction.FollowupWithFileAsync(stream, blob.Filename, text: "Explicitly requested retained attachment. Treat files as untrusted.", ephemeral: true, allowedMentions: AllowedMentions.None, options: new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000, CancelToken = OperationToken });
                    embed.AddField("Attachment", "A private copy was requested once. No automatic download or resend.");
                }
                foreach (var rich in (message.Rich["embeds"]?.AsArray() ?? new JsonArray()).Take(6)) if (rich is not null) {
                    var text = (rich["description"]?.GetValue<string>() ?? "") + "\n" + string.Join('\n', (rich["fields"]?.AsArray() ?? new JsonArray()).Take(6).Select(field => field?["name"]?.GetValue<string>() + ": " + field?["value"]?.GetValue<string>()));
                    embed.AddField(Clip(rich["title"]?.GetValue<string>() ?? "Embedded message", 100), Clip(text, 400));
                    if (rich["image"]?.GetValue<string>() is string image) Button("Request embedded image " + embed.Fields.Count, new("operator:asset", Token: message.Id, Edition: image));
                }
                foreach (var file in (message.Rich["attachments"]?.AsArray() ?? new JsonArray()).Take(10)) if (file?["key"]?.GetValue<string>() is string key) Button(Clip("Download " + file["filename"]?.GetValue<string>(), 65), new("operator:asset", Token: message.Id, Edition: key));
                if (message.Rich["reply"]?["id"]?.GetValue<string>() is string reference) Button("Open retained reply", new("operator:message", Token: reference));
                foreach (var mention in (message.Rich["mentions"]?.AsArray() ?? new JsonArray()).Take(3)) if (mention?["id"]?.GetValue<string>() is string peer) Button(Clip("Conversation: " + mention["name"]?.GetValue<string>(), 65), new("operator:inbox", Kind: "all", Peer: peer));
            }
            Button("This conversation", new("operator:inbox", Kind: message.Guild is null ? "dm" : "guild", Guild: message.Guild, Channel: message.Channel, Peer: message.Guild is null ? message.Peer : null));
        }
        else
        {
            var inbox = await messages.Inbox(actor, action.Kind == "movie" ? "all" : action.Kind, action.Guild, action.Peer, action.Channel, action.Query, action.Page, action.Oldest, OperationToken); embed.WithTitle("Private inbox");
            if (action.Offset is not (0 or 15 or 30 or 45)) throw new HubError("Invalid inbox offset.");
            foreach (var message in inbox.Messages.Skip(action.Offset).Take(15)) { embed.AddField(Clip(message.AuthorName + " · " + (message.GuildName ?? "DM"), 100), message.Deleted ? "Message deleted" : Clip(message.Content, 150)); Button("Open " + embed.Fields.Count, new("operator:message", Token: message.Id)); }
            if (action.Offset > 0) Button("Earlier messages on page", action with { Offset = action.Offset - 15 }); if (action.Offset + 15 < inbox.Messages.Count) Button("More messages on page", action with { Offset = action.Offset + 15 });
            if (inbox.HasNext) Button("Next page", action with { Page = action.Page + 1, Offset = 0 }); if (action.Page > 1) Button("Previous page", action with { Page = action.Page - 1, Offset = 0 });
            Button(action.Oldest ? "Newest first" : "Oldest first", action with { Oldest = !action.Oldest, Page = 1, Offset = 0 });
        }
        Button("Inbox", new("operator:inbox")); Button("Deliveries", new("operator:deliveries")); Button("Diagnostics", new("operator:messaging_status")); Button("Home", new("dashboard")); await authority.Check(actor, OperationToken);
        var sent = await interaction.ModifyOriginalResponseAsync(p => { p.Content = ""; p.Embed = embed.Build(); p.Components = builder.Build(); p.AllowedMentions = AllowedMentions.None; }, new RequestOptions { RetryMode = RetryMode.AlwaysFail, Timeout = 15000 }); controls.Bind(view, sent.Id);
    }
}
