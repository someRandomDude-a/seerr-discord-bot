using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

public sealed class DiscordMessaging(IServiceProvider services, SettingsService settings) : IDiscordMessaging, IDisposable
{
    private readonly HttpClient http = new(ServiceAddress.Handler()) { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private DiscordSocketClient Client => services.GetRequiredService<DiscordHost>().Connection ?? throw new HubError("Discord is unavailable.");
    private static RequestOptions Options(CancellationToken token) => new() { CancelToken = token, Timeout = 15000, RetryMode = RetryMode.AlwaysFail };
    private async Task<JsonNode> Read(string path, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/" + path); request.Headers.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordToken());
        using var response = await http.SendAsync(request, cancellation); response.EnsureSuccessStatusCode(); return SafeJson.Parse(await response.Content.ReadAsStringAsync(cancellation), 2 * 1024 * 1024);
    }
    private async Task<(IGuild Guild, IGuildUser Actor, IGuildUser Bot)> Guild(OperatorActor actor, string? id, CancellationToken cancellation)
    {
        if (!settings.Current.DiscordEnabled || id is null || !settings.Current.AllowedGuilds.Contains(id) || !ulong.TryParse(id, out var gid)) throw new HubError("This server is not allowed.");
        var guild = await ((IDiscordClient)Client.Rest).GetGuildAsync(gid, CacheMode.AllowDownload, Options(cancellation)) ?? throw new HubError("Server unavailable.");
        var user = await guild.GetUserAsync(ulong.Parse(actor.Discord), CacheMode.AllowDownload, Options(cancellation)) ?? throw new HubError("You no longer belong to this server.");
        var bot = await guild.GetCurrentUserAsync(CacheMode.AllowDownload, Options(cancellation)) ?? throw new HubError("Bot membership unavailable.");
        return (guild, user, bot);
    }
    public async Task<MessageDestination> Verify(OperatorActor actor, MessageTarget target, bool writing, bool files, CancellationToken cancellation)
    {
        var context = await Guild(actor, target.Guild, cancellation);
        if (target.Kind == "user")
        {
            var recipient = await context.Guild.GetUserAsync(ulong.Parse(target.Id), CacheMode.AllowDownload, Options(cancellation));
            if (recipient is null || recipient.IsBot) throw new HubError("Choose a human member of this allowed server.");
            return new(target, recipient.DisplayName);
        }
        if (target.Kind != "channel") throw new HubError("Invalid destination.");
        var channel = await ((IDiscordClient)Client.Rest).GetChannelAsync(ulong.Parse(target.Id), CacheMode.AllowDownload, Options(cancellation));
        if (channel is not IGuildChannel gc || gc.GuildId != context.Guild.Id || channel is not IMessageChannel) throw new HubError("Channel unavailable in this server.");
        var permissions = context.Actor.GetPermissions(gc); var botPermissions = context.Bot.GetPermissions(gc);
        if (!permissions.ViewChannel || !botPermissions.ViewChannel || !writing && (!permissions.ReadMessageHistory || !botPermissions.ReadMessageHistory)) throw new HubError("Conversation access denied.");
        if (gc is IThreadChannel thread)
        {
            if (writing && (thread.IsArchived || thread.IsLocked || !permissions.SendMessagesInThreads || !botPermissions.SendMessagesInThreads)) throw new HubError("This thread cannot receive messages.");
            if (thread.Type == ThreadType.PrivateThread)
            {
                // Guild/channel permissions alone do not establish membership of a private thread.
                if (!permissions.ManageThreads) _ = await Read("channels/" + target.Id + "/thread-members/" + actor.Discord, cancellation);
                if (!botPermissions.ManageThreads) _ = await Read("channels/" + target.Id + "/thread-members/" + Client.CurrentUser.Id, cancellation);
            }
        }
        else if (writing && (!permissions.SendMessages || !botPermissions.SendMessages)) throw new HubError("You or the bot cannot send to this channel.");
        if (files && (!permissions.AttachFiles || !botPermissions.AttachFiles)) throw new HubError("Attach Files permission is required for this destination.");
        return new(target, context.Guild.Name + " / #" + gc.Name);
    }
    public async Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor, CancellationToken cancellation)
    {
        var result = new List<MessageDestination>();
        foreach (var id in settings.Current.AllowedGuilds)
        {
            try
            {
                var context = await Guild(actor, id, cancellation);
                foreach (var channel in await context.Guild.GetChannelsAsync(CacheMode.AllowDownload, Options(cancellation)))
                {
                    if (result.Count >= 1000) throw new HubError("Destination list is too large. Select a server explicitly.");
                    if (channel is not IMessageChannel || !context.Actor.GetPermissions(channel).ViewChannel) continue;
                    try
                    {
                        var p = context.Actor.GetPermissions(channel); var b = context.Bot.GetPermissions(channel);
                        if (!b.ViewChannel || (channel is IThreadChannel ? !p.SendMessagesInThreads || !b.SendMessagesInThreads : !p.SendMessages || !b.SendMessages)) continue;
                        if (channel is IThreadChannel) result.Add(await Verify(actor, new("channel", channel.Id.ToString(), id), true, false, cancellation));
                        else result.Add(new(new("channel", channel.Id.ToString(), id), context.Guild.Name + " / #" + channel.Name));
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (HubError) { }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { /* No inaccessible server/channel names are returned as skipped labels. */ }
        }
        return result;
    }
    public async Task<IReadOnlyList<MessageTarget>> Members(OperatorActor actor, string guild, CancellationToken cancellation)
    {
        if (!settings.Current.Features.MembersIntent) throw new HubError("Members Intent is disabled.");
        _ = await Guild(actor, guild, cancellation); var result = new List<MessageTarget>(); var seen = new HashSet<string>(); ulong after = 0;
        for (var page = 0; page < 100; page++)
        {
            var rows = (await Read("guilds/" + guild + "/members?limit=1000&after=" + after, cancellation)).AsArray();
            foreach (var row in rows)
            {
                var id = row!["user"]!["id"]!.GetValue<string>(); if (!ulong.TryParse(id, out var n) || n <= after || !seen.Add(id)) throw new HubError("Member pagination changed. Preview again."); after = n;
                if (row["user"]!["bot"]?.GetValue<bool>() == true) continue;
                result.Add(new("user", id, guild)); if (result.Count > settings.Current.Features.MaxRecipients) throw new HubError("Recipient limit exceeded. No messages were sent.");
            }
            if (rows.Count < 1000) return result;
        }
        throw new HubError("Member scan incomplete. No messages were sent.");
    }
    public async Task<string> Send(MessageTarget target, string content, IReadOnlyList<MessageBlob> files, CancellationToken cancellation)
    {
        if (!settings.Current.DiscordEnabled) throw new HubError("Discord is disabled.");
        var client = Client; IMessageChannel channel;
        if (target.Kind == "user")
        {
            var recipient = await client.Rest.GetUserAsync(ulong.Parse(target.Id), Options(cancellation));
            if (recipient.IsBot) throw new HubError("Cannot send to a bot."); channel = await recipient.CreateDMChannelAsync(Options(cancellation));
        }
        else channel = await ((IDiscordClient)client.Rest).GetChannelAsync(ulong.Parse(target.Id), CacheMode.AllowDownload, Options(cancellation)) as IMessageChannel ?? throw new HubError("Channel unavailable.");
        var streams = files.Select(f => new MemoryStream(f.Bytes, false)).ToArray();
        try
        {
            var message = files.Count == 0 ? await channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None, options: Options(cancellation)) :
                await channel.SendFilesAsync(files.Select((file, i) => new FileAttachment(streams[i], file.Filename)), content, allowedMentions: AllowedMentions.None, options: Options(cancellation));
            return message.Id.ToString();
        }
        finally { foreach (var stream in streams) stream.Dispose(); }
    }
    public void Dispose() => http.Dispose();
}
