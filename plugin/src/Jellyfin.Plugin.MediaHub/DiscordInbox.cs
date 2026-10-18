using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.MediaHub;

public sealed partial class DiscordHost
{
    private static string Text(string? value, int limit) => value is null ? "" : value[..Math.Min(value.Length, limit)];
    private JsonNode Rich(SocketMessage message)
    {
        var attachments = new JsonArray(); var assets = new JsonObject(); var embeds = new JsonArray();
        var i = 0;
        foreach (var file in message.Attachments.Take(10))
        {
            var key = "attachment-" + i++;
            if (!MessageAssets.Allowed(file.Url)) continue;
            attachments.Add(new JsonObject { ["key"] = key, ["filename"] = Messaging.Filename(file.Filename), ["size"] = (long)file.Size });
            assets[key] = new JsonObject { ["url"] = file.Url, ["filename"] = Messaging.Filename(file.Filename) };
        }
        i = 0;
        foreach (var embed in message.Embeds.Take(10))
        {
            var row = new JsonObject { ["title"] = Text(embed.Title, 256), ["description"] = Text(embed.Description, 4096), ["footer"] = Text(embed.Footer?.Text, 2048) };
            var fields = new JsonArray(); foreach (var field in embed.Fields.Take(25)) fields.Add(new JsonObject { ["name"] = Text(field.Name, 256), ["value"] = Text(field.Value, 1024), ["inline"] = field.Inline }); row["fields"] = fields;
            var image = embed.Image?.ProxyUrl ?? embed.Image?.Url;
            if (image is not null && MessageAssets.Allowed(image)) { var key = "embed-" + i + "-image"; row["image"] = key; assets[key] = new JsonObject { ["url"] = image, ["filename"] = "image" }; }
            embeds.Add(row); i++;
        }
        var mentions = new JsonArray(); foreach (var user in message.MentionedUsers.Take(25)) mentions.Add(new JsonObject { ["id"] = user.Id.ToString(), ["name"] = Text(user.GlobalName ?? user.Username, 160) });
        var reply = message is SocketUserMessage replyMessage ? replyMessage.Reference : null;
        return new JsonObject { ["attachments"] = attachments, ["embeds"] = embeds, ["assets"] = assets, ["bot"] = message.Author.IsBot, ["mentions"] = mentions,
            ["reply"] = reply?.MessageId.IsSpecified == true ? new JsonObject { ["id"] = reply.MessageId.Value.ToString(), ["channel"] = reply.ChannelId.ToString() } : null };
    }
    private Task ReceiveMessage(SocketMessage message)
    {
        if (client?.CurrentUser is not { } bot || message.Author.Id == bot.Id) return Task.CompletedTask;
        var guild = (message.Channel as SocketGuildChannel)?.Guild;
        var reply = message is SocketUserMessage userMessage && userMessage.ReferencedMessage?.Author.Id == bot.Id;
        provider.GetRequiredService<Messaging>().Receive(new(message.Id.ToString(), message.Author.Id.ToString(), message.Author.GlobalName ?? message.Author.Username, guild?.Id.ToString(), guild?.Name, message.Channel.Id.ToString(), message.Channel.Name,
            message.Content ?? "", new JsonObject(), message.CreatedAt, null, false, "in", guild is null ? message.Author.Id.ToString() : null, null), Rich(message), guild is null, message.MentionedUsers.Any(u => u.Id == bot.Id), reply);
        return Task.CompletedTask;
    }
    private Task EditMessage(SocketMessage message)
    { provider.GetRequiredService<Messaging>().Edit(message.Id.ToString(), message.Channel.Id.ToString(), message.Content ?? "", Rich(message)); return Task.CompletedTask; }
}
