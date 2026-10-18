using System.Text.Json;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Read-only in-process adapter configuration. Legacy signed files remain untouched.</summary>
internal sealed class DiscordConfigurationOverlay(IServiceProvider services)
{
    private readonly object gate = new();
    private string? previous, bot, secret;
    internal HubSettings Apply(SettingsService settings, HubSettings retained)
    {
        try { return Read(settings, retained); }
        catch { return Disabled(retained); }
    }
    private static HubSettings Disabled(HubSettings retained) => retained with { AdapterConfigurationPresent = true, AdapterConfigurationUnavailable = true, DiscordEnabled = false,
            DiscordTokenEncrypted = "", AllowedGuilds = [], Features = retained.Features with { DiscordApplicationId = "", DiscordClientSecretEncrypted = "", ActivityEnabled = false,
                Operators = [], SeerrOperators = false, MembersIntent = false, MessageContent = false, InboxChannels = [] } };
    private HubSettings Read(SettingsService settings, HubSettings retained)
    {
        var snapshot = services.GetKeyedService<Func<string>>(CoreApi.DiscordConfigurationKey);
        var migrated = settings.Protection.DiscordConfigurationMigrated();
        if (snapshot is null) return migrated ? Disabled(retained) : retained;
        var encoded = snapshot();
        if (encoded == "null") return migrated ? Disabled(retained) : retained with { AdapterConfigurationPresent = true }; // Signed legacy behavior until explicit migration; adapter owns new edits.
        if (!migrated) throw new HubError("Adapter configuration has no matching authenticated core custody record. Restore matching stopped state.");
        var value = SafeJson.Read<DiscordConfigurationSnapshot>(encoded, 65536);
        if (value.Revision < 1 || value.AllowedGuilds is null || value.Operators is null || value.InboxChannels is null) throw new HubError("Adapter configuration could not be verified.");
        lock (gate)
        {
            if (previous != encoded)
            {
                bot = settings.Protect(value.BotToken); secret = settings.Protect(value.ClientSecret); previous = encoded;
            }
            var result = retained with { AdapterConfigurationPresent = true, AdapterRevision = value.Revision, DiscordEnabled = value.DiscordEnabled, AllowedGuilds = value.AllowedGuilds, DiscordTokenEncrypted = bot!,
                Features = retained.Features with { DiscordApplicationId = value.ApplicationId, DiscordClientSecretEncrypted = secret!, ActivityEnabled = value.ActivityEnabled,
                    OAuthPkce = value.OAuthPkce, MessageContent = value.MessageContent, MembersIntent = value.MembersIntent, Operators = value.Operators,
                    SeerrOperators = value.SeerrOperators, InboxChannels = value.InboxChannels, MessageRetentionDays = value.MessageRetentionDays, MaxRecipients = value.MaxRecipients } };
            _ = result.RuntimeRevision; return result;
        }
    }
}
