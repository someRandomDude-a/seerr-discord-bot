using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Jellyfin.Plugin.MediaHub.DiscordIntegration;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Plugin-owned outbound Discord gateway using Jellyfin's supervised service lifetime.</summary>
public sealed partial class DiscordHost(IServiceProvider provider, ILogger<DiscordHost> log) : BackgroundService
{
    private HubStore store => provider.GetRequiredService<HubStore>();
    private HostIdentity identity => provider.GetRequiredService<HostIdentity>();
    private SettingsService settings => provider.GetRequiredService<SettingsService>();
    private DiscordSocketClient? client;
    private int connectionRevision = -1;
    internal DiscordSocketClient? Connection => settings.Current is { DiscordEnabled: true } s && s.Revision == connectionRevision && client is { ConnectionState: ConnectionState.Connected } ? client : null;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> nextAction = new();
    private readonly SemaphoreSlim eventSlots = new(8);
    private readonly AsyncLocal<CancellationToken> operation = new();
    private CancellationToken lifetime;
    private CancellationToken OperationToken => operation.Value;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lifetime = stoppingToken;
        var revision = -1;
        var retrySeconds = 5;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var config = settings.Current;
                    if (!provider.GetRequiredService<IntegrationDependencies>().Ready)
                    {
                        await Disconnect(); revision = -1;
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                        continue;
                    }
                    retrySeconds = 5;
                    if (config.Revision != revision)
                    {
                        var configured = true;
                        await Disconnect();
                        if (config.DiscordEnabled)
                        {
                            try
                            {
                                var connection = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds | GatewayIntents.DirectMessages | GatewayIntents.GuildMessages |
                                    (config.Features.MessageContent ? GatewayIntents.MessageContent : 0) | (config.Features.MembersIntent ? GatewayIntents.GuildMembers : 0), MessageCacheSize = 0, ConnectionTimeout = 15000 });
                                client = connection;
                                     connection.Ready += () => GuardEvent(connection, config.Revision, () => Register(connection));
                                     connection.SlashCommandExecuted += command => GuardEvent(connection, config.Revision, () => Dispatch(command));
                                     connection.ButtonExecuted += button => GuardEvent(connection, config.Revision, () => Dispatch(button));
                                     connection.MessageReceived += message => GuardEvent(connection, config.Revision, () => ReceiveMessage(message));
                                     connection.MessageUpdated += (_, message, _) => GuardEvent(connection, config.Revision, () => EditMessage(message));
                                     connection.MessageDeleted += (message, channel) => GuardEvent(connection, config.Revision, () => { provider.GetRequiredService<Messaging>().Delete(message.Id.ToString(), channel.Id.ToString()); return Task.CompletedTask; });
                                     connection.MessagesBulkDeleted += (messages, channel) => GuardEvent(connection, config.Revision, () => { foreach (var message in messages.Take(100)) provider.GetRequiredService<Messaging>().Delete(message.Id.ToString(), channel.Id.ToString()); return Task.CompletedTask; });
                                await connection.LoginAsync(TokenType.Bot, settings.DiscordToken()).WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
                                 await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
                                 connectionRevision = config.Revision;
                            }
                            catch { configured = false; log.SafeWarning("Media Hub Discord startup failed. Check the plugin settings and outbound connectivity."); }
                        }
                        revision = configured ? config.Revision : -1;
                    }
                    using var deliveryBudget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); deliveryBudget.CancelAfter(TimeSpan.FromSeconds(45));
                    try
                    {
                    if (client is { ConnectionState: ConnectionState.Connected } senderConnection)
                        await provider.GetRequiredService<ConsentDelivery>().Deliver(async (discord, notice, cancellation) =>
                        {
                            if (!ReferenceEquals(Connection, senderConnection)) throw new HubError("Discord configuration changed.");
                            var recipient = await senderConnection.Rest.GetUserAsync(ulong.Parse(discord), new RequestOptions { CancelToken = cancellation, RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
                            var buttons = new ComponentBuilder().WithButton("Agree", "mediahub:consent:" + notice.Id + ":yes", ButtonStyle.Success)
                                .WithButton("Decline", "mediahub:consent:" + notice.Id + ":no", ButtonStyle.Secondary).Build();
                            var title = notice.Title.Length > 256 ? notice.Title[..256] : notice.Title;
                            var personalMessage = System.Text.RegularExpressions.Regex.Replace(Clip(notice.Message, 1500), @"([\\`*_{}\[\]()#+\-.!|>~])", @"\$1");
                            await recipient.SendMessageAsync(embed: new EmbedBuilder().WithTitle(title).WithDescription(notice.Message == "Allow deletion?" ? notice.Message : "Allow deletion?\n" + personalMessage).Build(),
                                components: buttons, allowedMentions: AllowedMentions.None,
                                options: new RequestOptions { CancelToken = cancellation, RetryMode = RetryMode.AlwaysFail, Timeout = 15000 });
                         }, deliveryBudget.Token);
                     if (Connection is not null) await provider.GetRequiredService<Messaging>().Deliver(deliveryBudget.Token);
                    if (client is { ConnectionState: ConnectionState.Connected } notificationConnection)
                        await provider.GetRequiredService<NotificationFeed>().Deliver(async (discord, notice, cancellation) =>
                        {
                            if (!ReferenceEquals(Connection, notificationConnection)) throw new HubError("Discord configuration changed.");
                            var options = new RequestOptions { CancelToken = cancellation, RetryMode = RetryMode.AlwaysFail, Timeout = 15000 };
                            var recipient = await notificationConnection.Rest.GetUserAsync(ulong.Parse(discord), options);
                            await recipient.SendMessageAsync(embed: new EmbedBuilder().WithTitle(Clip(notice.Title, 200)).WithDescription(notice.Status).Build(),
                                allowedMentions: AllowedMentions.None, options: options);
                         }, deliveryBudget.Token);
                    }
                    catch (OperationCanceledException) when (deliveryBudget.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { }
                }
                catch
                {
                    log.SafeWarning("Media Hub configuration/state could not be loaded. Discord remains unavailable; Jellyfin will continue running. Restore the plugin data/key or check permissions.");
                    await Disconnect(); revision = -1; retrySeconds = Math.Min(60, retrySeconds * 2);
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { log.SafeWarning("Media Hub Discord worker stopped after a plugin failure. Jellyfin remains available."); }
        finally { await Disconnect(); }
    }
    private async Task GuardEvent(DiscordSocketClient connection, int expectedRevision, Func<Task> action)
    {
        if (!eventSlots.Wait(0)) return; // Never queue an unbounded gateway backlog inside Jellyfin.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime); budget.CancelAfter(TimeSpan.FromSeconds(30));
        var previous = operation.Value; operation.Value = budget.Token;
        try
        {
            if (!ReferenceEquals(client, connection) || settings.Current is not { DiscordEnabled: true } current || current.Revision != expectedRevision) return;
            await action();
        }
        catch { log.SafeWarning("Media Hub Discord event could not be completed. No automatic resend was performed."); }
        finally { operation.Value = previous; eventSlots.Release(); }
    }
    private async Task Disconnect()
    {
        var connection = client; client = null; connectionRevision = -1;
        if (connection is null) return;
        try { await connection.StopAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { log.SafeWarning("Media Hub Discord disconnect was not confirmed; disposing only the plugin connection."); }
        finally
        {
            try { connection.Dispose(); }
            catch { log.SafeWarning("Media Hub Discord connection cleanup failed; host services were not disposed."); }
        }
    }
    private async Task Register(DiscordSocketClient connection)
    {
        foreach (var guildId in settings.Current.AllowedGuilds)
        {
            var guild = connection.GetGuild(ulong.Parse(guildId)); if (guild is null) continue;
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder().WithName("hub-link")
                .WithDescription("Link your Discord account to Jellyfin").Build());
            foreach (var command in new[] { ("hub-item", "View a Jellyfin item"), ("hub-follow", "Save a Jellyfin item and subscribe") })
                await guild.CreateApplicationCommandAsync(new SlashCommandBuilder().WithName(command.Item1).WithDescription(command.Item2)
                    .AddOption("item", ApplicationCommandOptionType.String, "Jellyfin item ID", true).Build());
            await RegisterMedia(guild);
            await RegisterOperatorCommands(guild);
        }
    }
    private void Limit(ulong user)
    {
        lock (nextAction)
        {
            var key = user.ToString(); var now = DateTimeOffset.UtcNow;
            if (nextAction.TryGetValue(key, out var until) && until > now) throw new HubError("Please wait a few seconds before trying again.");
            nextAction[key] = now.AddSeconds(3);
            if (nextAction.Count > 10000) foreach (var old in nextAction.Where(pair => pair.Value <= now)) nextAction.TryRemove(old.Key, out _);
        }
    }
    private async Task Dispatch(SocketInteraction interaction)
    {
        await interaction.DeferAsync(ephemeral: true);
        try
        {
            if (!settings.Current.DiscordEnabled) throw new HubError("This integration is currently disabled.");
            Limit(interaction.User.Id);
            if (interaction is SocketSlashCommand slash) await Slash(slash);
            else if (interaction is SocketMessageComponent control && control.Data.CustomId.StartsWith("mediahub:control:", StringComparison.Ordinal)) await MediaClick(control);
            else if (interaction is SocketMessageComponent button) await LegacyOrConsentButton(button);
        }
        catch (WatchPartyFailure error) { await interaction.FollowupAsync(error.Message, ephemeral: true, allowedMentions: AllowedMentions.None); }
        catch (HubError error) { await interaction.FollowupAsync(error.Message, ephemeral: true, allowedMentions: AllowedMentions.None); }
        catch { await interaction.FollowupAsync("Not confirmed. Check the result before retrying.", ephemeral: true, allowedMentions: AllowedMentions.None); }
    }
    private async Task Slash(SocketSlashCommand interaction)
    {
        if (interaction.GuildId is not ulong guild || !settings.Current.AllowedGuilds.Contains(guild.ToString()))
            throw new HubError("Use this command in an allowed Discord server.");
        if (OperatorCommands.Contains(interaction.CommandName)) { await OperatorSlash(interaction); return; }
        if (MediaCommands.Contains(interaction.CommandName)) { await MediaSlash(interaction); return; }
        if (interaction.CommandName is not ("hub-link" or "hub-item" or "hub-follow")) throw new HubError("Unknown command.");
        if (interaction.CommandName == "hub-link")
        {
            var code = store.BeginLink(interaction.User.Id.ToString());
            var buttons = settings.Current.Features.DiscordApplicationId.Length > 0 ? new ComponentBuilder().WithButton("Link in Jellyfin", style: ButtonStyle.Link, url: provider.GetRequiredService<LinkFlow>().HandoffUrl(code)).Build() : null;
            await interaction.FollowupAsync($"Open Media Hub in Jellyfin to approve your account (`{interaction.User.Id}`).\nFallback code: `{code}`\nExpires in 5 minutes. Only approve links/codes you requested.", components: buttons, ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        var user = await identity.Discord(interaction.User.Id.ToString());
        if (interaction.CommandName is "hub-item" or "hub-follow")
        {
            if (!Guid.TryParse(interaction.Data.Options.FirstOrDefault(option => option.Name == "item")?.Value?.ToString(), out var itemId)) throw new HubError("Enter a Jellyfin item ID.");
            var media = provider.GetRequiredService<IViewerMedia>().Find(user, itemId) ?? throw new HubError("Item unavailable.");
            if (interaction.CommandName == "hub-follow")
            {
                var personal = provider.GetRequiredService<PersonalService>();
                var saved = personal.Bell(user, itemId, true, personal.State(user).Revision);
                await interaction.FollowupAsync("Saved and subscribed." + (saved.Activity?.JellyfinUpdated == false ? " Activity saved locally; Jellyfin timestamp update unavailable." : ""), ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            else
            {
                var result = provider.GetRequiredService<MediaActivity>().Record(user, itemId, MediaInteraction.Details);
                await interaction.FollowupAsync(media.Title + (result.JellyfinUpdated ? "" : "\nActivity saved locally; Jellyfin update unavailable."), ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            return;
        }
    }
    private async Task LegacyOrConsentButton(SocketMessageComponent interaction)
    {
        const string consentPrefix = "mediahub:consent:";
        if (interaction.Data.CustomId.StartsWith(consentPrefix, StringComparison.Ordinal))
        {
            if (interaction.GuildId is not null) throw new HubError("Use the private notification.");
            var parts = interaction.Data.CustomId[consentPrefix.Length..].Split(':');
            if (parts.Length != 2 || parts[1] is not ("yes" or "no")) throw new HubError("Invalid consent control.");
            var viewer = await identity.Discord(interaction.User.Id.ToString());
            await provider.GetRequiredService<DeletionQueue>().Respond(viewer, parts[0], parts[1] == "yes", OperationToken);
            await interaction.FollowupAsync(parts[1] == "yes" ? "Agreed." : "Deletion blocked while your activity is recent.", ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        if (interaction.Data.CustomId.StartsWith("mediahub:join:", StringComparison.Ordinal))
            throw new HubError("This legacy invitation is retired. Generate a new provider invitation with /watchparty.", "watchparty_unavailable");
        throw new HubError("Unknown control.");
    }
}
