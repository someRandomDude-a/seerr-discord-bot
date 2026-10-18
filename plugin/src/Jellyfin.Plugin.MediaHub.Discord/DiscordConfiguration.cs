using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.MediaHub;

public sealed record DiscordConfigurationUpdate(int Revision, bool ImportLegacy, bool DiscordEnabled, string? BotToken,
    string[] AllowedGuilds, string ApplicationId, string? ClientSecret, bool ActivityEnabled, bool OAuthPkce,
    bool MessageContent, bool MembersIntent, string[] Operators, bool SeerrOperators, string[] InboxChannels,
    int MessageRetentionDays, int MaxRecipients);

/// <summary>Adapter-owned encrypted settings only. No SQLite, host configuration, eager I/O or implicit legacy activation.</summary>
public sealed class DiscordConfiguration
{
    private sealed record Envelope(int Format, int Revision, string Ciphertext);
    private readonly string directory;
    private readonly Func<DiscordConfigurationSnapshot> legacy;
    private readonly Action confirmMigration;
    private readonly Func<bool> wasMigrated;
    private readonly object gate = new();
    private int highest;
    private byte[]? retainedKey;
    private const int Maximum = 65536;
    public DiscordConfiguration(IApplicationPaths paths, Rpc rpc) : this(Path.Combine(paths.DataPath, "media-hub-discord"),
        () => rpc.Server<DiscordConfigurationSnapshot>("transport.legacyDiscordConfiguration"), () => _ = rpc.Server<bool>("transport.confirmDiscordMigration"),
        () => rpc.Server<bool>("transport.discordMigrationState")) { }
    internal DiscordConfiguration(string directory, Func<DiscordConfigurationSnapshot> legacy, Action? confirmMigration = null, Func<bool>? wasMigrated = null)
    { this.directory = directory; this.legacy = legacy; this.confirmMigration = confirmMigration ?? (() => { }); this.wasMigrated = wasMigrated ?? (() => false); }
    private string File(string name) => StateFiles.In(directory, name);
    private byte[] Key()
    {
        var key = StateFiles.ReadBounded(directory, "secrets.key", 32);
        if (key.Length != 32 || retainedKey is not null && !CryptographicOperations.FixedTimeEquals(key, retainedKey))
            throw new HubError("Discord configuration key is missing or changed. Restore matching stopped state; nothing was reset.");
        retainedKey ??= key.ToArray(); return key;
    }
    private DiscordConfigurationSnapshot? Load()
    {
        if (!System.IO.File.Exists(File("settings.json")))
        {
            if (highest != 0 || System.IO.File.Exists(File("secrets.key")) || System.IO.File.Exists(File("settings.previous.json")))
                throw new HubError("Retained Discord configuration is incomplete. No defaults or legacy settings were substituted.");
            return null;
        }
        var envelope = SafeJson.Read<Envelope>(Encoding.UTF8.GetString(StateFiles.ReadBounded(directory, "settings.json", Maximum)), Maximum);
        if (envelope.Format != 1 || envelope.Revision < 1 || envelope.Revision < highest || envelope.Ciphertext.Length > Maximum)
            throw new HubError("Discord configuration format/revision is invalid. Retained files were not changed.");
        try
        {
            var bytes = Convert.FromBase64String(envelope.Ciphertext);
            if (bytes.Length < 28) throw new HubError("Invalid Discord configuration envelope.");
            var plaintext = new byte[bytes.Length - 28]; using var aes = new AesGcm(Key(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes("MediaHub.Discord.Settings.v1|" + envelope.Revision));
            var value = SafeJson.Read<DiscordConfigurationSnapshot>(Encoding.UTF8.GetString(plaintext), Maximum);
            if (value.Revision != envelope.Revision) throw new HubError("Discord configuration revision could not be authenticated.");
            Validate(value); highest = value.Revision; return value;
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new HubError("Discord configuration cannot be authenticated. Restore matching stopped state; no key or configuration was reset."); }
    }
    internal static void Validate(DiscordConfigurationSnapshot value)
    {
        static bool Id(string? id) => id is { Length: > 0 and <= 20 } && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var number) && number != 0;
        static void Ids(string[]? ids) { if (ids is null || ids.Length > 100 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !Id(id))) throw new HubError("Specify at most 100 unique Discord IDs per allowlist."); }
        if (value.Revision < 0 || value.BotToken is null || value.ClientSecret is null || value.ApplicationId is null || value.BotToken.Length > 4096 || value.ClientSecret.Length > 4096 ||
            value.MessageRetentionDays is < 1 or > 365 || value.MaxRecipients is < 1 or > 1000) throw new HubError("Invalid Discord configuration or storage budget.");
        Ids(value.AllowedGuilds); Ids(value.Operators); Ids(value.InboxChannels);
        if (value.BotToken.Length > 0) ServiceAddress.Key(value.BotToken);
        if (value.ClientSecret.Length > 0) ServiceAddress.Key(value.ClientSecret);
        if ((value.ApplicationId.Length == 0) != (value.ClientSecret.Length == 0) || value.ApplicationId.Length > 0 && !Id(value.ApplicationId) || value.ActivityEnabled && value.ApplicationId.Length == 0)
            throw new HubError("Configure the matching Discord application ID and client secret before enabling Activity.");
        if (value.DiscordEnabled && (value.BotToken.Length == 0 || value.AllowedGuilds.Length == 0)) throw new HubError("Enabling the bot requires a token and explicit server allowlist.");
    }
    internal DiscordConfigurationSnapshot? Snapshot() { lock (gate) return Load(); }
    public string SnapshotJson() { lock (gate) return JsonSerializer.Serialize(Load()); }
    private DiscordConfigurationSnapshot LegacyForReview()
    {
        // Older signed settings permitted repeated allowlist entries. Present the
        // same set semantics for explicit review, never rewrite the legacy bytes.
        var value = legacy(); return value with { AllowedGuilds = value.AllowedGuilds.Distinct(StringComparer.Ordinal).ToArray(),
            Operators = value.Operators.Distinct(StringComparer.Ordinal).ToArray(), InboxChannels = value.InboxChannels.Distinct(StringComparer.Ordinal).ToArray() };
    }
    public object Review()
    {
        DiscordConfigurationSnapshot? local; lock (gate) local = Load();
        if (local is null && wasMigrated()) throw new HubError("Discord configuration is unavailable after migration. Restore matching stopped core/adapter backups; no legacy settings or replacement key were substituted.");
        lock (gate)
        {
            local = Load(); var value = local ?? LegacyForReview(); Validate(value);
            return new { Revision = local?.Revision ?? 0, MigrationReviewRequired = local is null, value.DiscordEnabled, value.AllowedGuilds, value.ApplicationId,
                value.ActivityEnabled, value.OAuthPkce, value.MessageContent, value.MembersIntent, value.Operators, value.SeerrOperators, value.InboxChannels,
                value.MessageRetentionDays, value.MaxRecipients, HasBotToken = value.BotToken.Length > 0, HasClientSecret = value.ClientSecret.Length > 0 };
        }
    }
    public void Update(DiscordConfigurationUpdate update)
    {
        StateFiles.Check(directory);
        if (!System.IO.File.Exists(File("settings.json")) && wasMigrated()) throw new HubError("Migrated Discord settings are missing. Restore matching stopped backups; no key or defaults were recreated.");
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        using var lease = StateFiles.Lease(directory, "settings.lock");
        DiscordConfigurationSnapshot? current; DiscordConfigurationSnapshot next; byte[] plaintext;
        lock (gate)
        {
            current = Load();
            if (update.Revision != (current?.Revision ?? 0)) throw new HubError("Discord settings changed. Reload and review before saving.");
            if (current is null && !update.ImportLegacy) throw new HubError("Explicitly review/import retained settings before the first adapter save. Nothing was activated.");
            var previous = current ?? LegacyForReview(); Validate(previous);
            next = new DiscordConfigurationSnapshot(checked((current?.Revision ?? 0) + 1), update.DiscordEnabled, update.BotToken ?? previous.BotToken,
                update.AllowedGuilds, update.ApplicationId, update.ClientSecret ?? previous.ClientSecret, update.ActivityEnabled, update.OAuthPkce,
                update.MessageContent, update.MembersIntent, update.Operators, update.SeerrOperators, update.InboxChannels, update.MessageRetentionDays, update.MaxRecipients);
            Validate(next); plaintext = JsonSerializer.SerializeToUtf8Bytes(next);
            if (plaintext.Length > 45000) throw new HubError("Combined Discord settings exceed their supported size. Nothing was saved.");
            StateFiles.RequireSpace(directory, Maximum * 2);
        }
        // Keep the private writer lease, but never hold the snapshot-reader lock
        // while calling the core: it may be finishing a DB transaction that reads
        // this adapter snapshot. A torn migration must disable, never fall back.
        if (current is null) {
            if (wasMigrated()) throw new HubError("Migration custody changed. Restore matching stopped backups before any further save.");
            confirmMigration();
        }
        lock (gate)
        {
            if (JsonSerializer.Serialize(Load()) != JsonSerializer.Serialize(current)) throw new HubError("Discord settings changed during migration. Nothing was overwritten.");
            if (current is null) StateFiles.Publish(directory, "secrets.key", RandomNumberGenerator.GetBytes(32));
            var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var ciphertext = new byte[plaintext.Length]; using var aes = new AesGcm(Key(), 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("MediaHub.Discord.Settings.v1|" + next.Revision));
            var encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, next.Revision, Convert.ToBase64String([.. nonce, .. tag, .. ciphertext])));
            if (encoded.Length > Maximum) throw new HubError("Encrypted Discord settings exceed their supported size.");
            StateFiles.Publish(directory, "settings.json", encoded, "settings.previous.json"); highest = next.Revision;
        }
    }
}
