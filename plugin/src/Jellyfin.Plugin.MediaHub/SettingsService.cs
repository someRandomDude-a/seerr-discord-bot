using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaHub;

public sealed record HubSettings
{
    public int FormatVersion { get; init; } = 3;
    public string Integrity { get; init; } = "";
    public int RecentActivityDays { get; init; } = 14;
    public int DeletionDelayDays { get; init; } = 2;
    public bool AutomaticDeletion { get; init; }
    public int Revision { get; init; }
    public bool DiscordEnabled { get; init; }
    public string PublicJellyfinUrl { get; init; } = "";
    public string[] AllowedGuilds { get; init; } = [];
    public string SeerrUrl { get; init; } = "";
    public string DiscordTokenEncrypted { get; init; } = "";
    public string SeerrKeyEncrypted { get; init; } = "";
    public ExtendedSettings Features { get; init; } = new();
}
public sealed record ExtraServiceSettings(string Url = "", string KeyEncrypted = "", string Root = "", int Quality = 0, int Metadata = 0, bool Requests = false, int DailyLimit = 10);
public sealed record ArrDestination(string Service, int Instance, string Url);
public sealed record ExtendedSettings
{
    public ExtraServiceSettings Lidarr { get; init; } = new();
    public ExtraServiceSettings Readarr { get; init; } = new();
    public string[] Operators { get; init; } = [];
    public bool SeerrOperators { get; init; }
    public bool MessageContent { get; init; }
    public bool MembersIntent { get; init; }
    public string[] InboxChannels { get; init; } = [];
    public int MessageRetentionDays { get; init; } = 7;
    public int MaxRecipients { get; init; } = 100;
    public string DiscordApplicationId { get; init; } = "";
    public string DiscordClientSecretEncrypted { get; init; } = "";
    public bool ActivityEnabled { get; init; }
    public bool OAuthPkce { get; init; } = true;
    public ArrDestination[] ArrDestinations { get; init; } = [];
    // Absent optional extensions must not change existing format-3 HMAC serialization.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotificationHookKeyEncrypted { get; init; }
}
public sealed record ExtraServiceUpdate(string Url, string? Key, string Root, int Quality, int Metadata, bool Requests, int DailyLimit = 10);
public sealed record ExtendedUpdate(ExtraServiceUpdate Lidarr, ExtraServiceUpdate Readarr, string[] Operators, bool SeerrOperators = false,
    bool MessageContent = false, bool MembersIntent = false, string[]? InboxChannels = null, int MessageRetentionDays = 7, int MaxRecipients = 100,
    string? DiscordApplicationId = null, string? DiscordClientSecret = null, bool? ActivityEnabled = null, bool? OAuthPkce = null, ArrDestination[]? ArrDestinations = null, string? NotificationHookKey = null);
public sealed record SettingsUpdate(int Revision, bool DiscordEnabled, string PublicJellyfinUrl, string[] AllowedGuilds,
    string SeerrUrl, string? DiscordToken, string? SeerrKey, int? RecentActivityDays = null, int? DeletionDelayDays = null, bool? AutomaticDeletion = null, ExtendedUpdate? Features = null);

/// <summary>Secrets never enter Jellyfin's generic plugin configuration/readback API.</summary>
public sealed class SettingsService
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string path;
    private readonly string keyPath;
    private readonly byte[] key;
    private readonly Action<string>? checkpoint;
    private HubSettings settings;
    internal StateIntegrity Protection { get; }
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public SettingsService(HubStore store) : this(store, null) { }
    internal SettingsService(HubStore store, Action<string>? checkpoint)
    {
        directory = store.DirectoryPath; this.checkpoint = checkpoint;
        path = StateFiles.In(directory, "settings.json");
        keyPath = StateFiles.In(directory, "secrets.key");
        var createdKey = false;
        if (!File.Exists(keyPath))
        {
            if (File.Exists(path) || File.Exists(StateFiles.In(directory, "settings.previous.json")) || File.Exists(StateFiles.In(directory, "state-auth.json")) || File.Exists(StateFiles.In(directory, "state-auth.previous.json")) || store.HasCredentials())
                throw new HubError("Plugin encryption key is missing. Restore the backup; do not overwrite retained configuration.");
            using var lease = StateFiles.Lease(directory, "settings.lock");
            if (!File.Exists(keyPath))
            {
                if (File.Exists(path) || File.Exists(StateFiles.In(directory, "settings.previous.json")) || File.Exists(StateFiles.In(directory, "state-auth.json")) || File.Exists(StateFiles.In(directory, "state-auth.previous.json")) || store.HasCredentials())
                    throw new HubError("Plugin state changed during key initialization. Recheck retained state; no key was replaced.");
                StateFiles.Publish(directory, "secrets.key", RandomNumberGenerator.GetBytes(32), checkpoint: checkpoint);
                createdKey = true;
            }
        }
        key = StateFiles.ReadBounded(directory, "secrets.key", 32);
        if (key.Length != 32) throw new HubError("Invalid plugin encryption key.");
        settings = Load(allowUnsigned: true); // Only an elevated review may activate older unsigned settings.
        Protection = new StateIntegrity(store, this); Protection.Initialize(createdKey);
    }
    public HubSettings Current { get { lock (gate) { settings = Load(); return settings with { AllowedGuilds = [.. settings.AllowedGuilds], Features = settings.Features with { Operators = [.. settings.Features.Operators], InboxChannels = [.. settings.Features.InboxChannels], ArrDestinations = [.. settings.Features.ArrDestinations] } }; } } }
    public object Public() { HubSettings s; lock (gate) s = Load(allowUnsigned: true); s = ReviewOnly(s); return new { s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, ConfigurationReviewRequired = s.FormatVersion < 3,
        s.RecentActivityDays, s.DeletionDelayDays, s.AutomaticDeletion, Features = new { Lidarr = ExtraPublic(s.Features.Lidarr), Readarr = ExtraPublic(s.Features.Readarr), s.Features.Operators,
            s.Features.SeerrOperators, s.Features.MessageContent, s.Features.MembersIntent, s.Features.InboxChannels, s.Features.MessageRetentionDays, s.Features.MaxRecipients,
            s.Features.DiscordApplicationId, s.Features.ActivityEnabled, s.Features.OAuthPkce, s.Features.ArrDestinations, HasNotificationHookKey = !string.IsNullOrEmpty(s.Features.NotificationHookKeyEncrypted), HasDiscordClientSecret = s.Features.DiscordClientSecretEncrypted.Length > 0 },
        HasDiscordToken = s.DiscordTokenEncrypted.Length > 0, HasSeerrKey = s.SeerrKeyEncrypted.Length > 0, FeatureParityComplete = false }; }
    private static string Url(string value, bool https)
    {
        if (value is null) throw new HubError("Specify the service address, or an empty string to disable it.");
        return value.Length == 0 ? "" : ServiceAddress.Url(value, https);
    }
    private static HubSettings ReviewOnly(HubSettings s) => s.FormatVersion >= 3 ? s : s with { DiscordEnabled = false, AutomaticDeletion = false,
        Features = s.Features with { ActivityEnabled = false, Lidarr = s.Features.Lidarr with { Requests = false }, Readarr = s.Features.Readarr with { Requests = false } } };
    private static object ExtraPublic(ExtraServiceSettings s) => new { s.Url, s.Root, s.Quality, s.Metadata, s.Requests, s.DailyLimit, HasKey = s.KeyEncrypted.Length > 0 };
    private ExtraServiceSettings Extra(ExtraServiceUpdate u, ExtraServiceSettings current)
    {
        if (u is null) throw new HubError("Specify the media service settings.");
        if (u.Key?.Length > 4096) throw new HubError("Service keys must not exceed 4096 characters.");
        var value = new ExtraServiceSettings(Url(u.Url, false), u.Key is null ? current.KeyEncrypted : Protect(u.Key), u.Root, u.Quality, u.Metadata, u.Requests, u.DailyLimit);
        ValidateExtra(value); return value;
    }
    private void ValidateExtra(ExtraServiceSettings s)
    {
        if (s is null || s.Url is null || s.KeyEncrypted is null || s.Root is null || s.Root.Length > 2048 || s.Root.Any(char.IsControl) || s.Quality < 0 || s.Metadata < 0 || s.DailyLimit is < 1 or > 100)
            throw new HubError("Invalid media service settings.");
        _ = Url(s.Url, false); var key = Unprotect(s.KeyEncrypted); if (key.Length > 0) ServiceAddress.Key(key);
        if ((s.Url.Length == 0) != (s.KeyEncrypted.Length == 0) || s.Requests && (s.Url.Length == 0 || s.Root.Length == 0 || s.Quality == 0 || s.Metadata == 0))
            throw new HubError("Configure the service URL/key and request root/profiles before allowing requests.");
    }
    private void ValidateFeatures(ExtendedSettings f)
    {
        if (f is null || f.Operators is null || f.InboxChannels is null || f.Operators.Length > 100 || f.InboxChannels.Length > 100 ||
            f.Operators.Concat(f.InboxChannels).Any(id => !ulong.TryParse(id, out var value) || value == 0) || f.MessageRetentionDays is < 1 or > 365 || f.MaxRecipients is < 1 or > 1000)
            throw new HubError("Invalid operator/messaging settings.");
        ValidateExtra(f.Lidarr); ValidateExtra(f.Readarr);
        if (f.DiscordApplicationId is null || f.DiscordClientSecretEncrypted is null || f.DiscordApplicationId.Length > 0 && (!ulong.TryParse(f.DiscordApplicationId, out var app) || app == 0) ||
            (f.DiscordApplicationId.Length == 0) != (f.DiscordClientSecretEncrypted.Length == 0)) throw new HubError("Configure the Discord application ID and OAuth client secret together.");
        _ = Unprotect(f.DiscordClientSecretEncrypted);
        if (f.ActivityEnabled && f.DiscordApplicationId.Length == 0) throw new HubError("Activity needs a configured Discord application.");
        if (f.ArrDestinations is null || f.ArrDestinations.Length > 100 || f.ArrDestinations.Any(d => d is null || d.Service is not ("radarr" or "sonarr") || d.Instance < 0) ||
            f.ArrDestinations.Select(d => (d.Service, d.Instance)).Distinct().Count() != f.ArrDestinations.Length) throw new HubError("Specify unique approved Arr service/instance destinations (maximum 100).");
        foreach (var d in f.ArrDestinations) _ = ServiceAddress.Url(d.Url);
        if (f.NotificationHookKeyEncrypted is { } encrypted)
        {
            var hook = Unprotect(encrypted);
            if (hook.Length is < 32 or > 256 || hook.Any(char.IsControl)) throw new HubError("Notification signing keys require 32–256 non-control characters, or explicit removal.");
        }
    }
    internal void RequireArr(string service, int instance, string url)
    {
        var canonical = ServiceAddress.Url(url);
        if (!Current.Features.ArrDestinations.Any(d => d.Service == service && d.Instance == instance && ServiceAddress.Url(d.Url) == canonical))
            throw new HubError("This Arr service/instance/address is not approved in Media Hub administrator settings. No Arr connection or change was made.");
    }
    private void CheckKey()
    {
        StateFiles.In(directory, "secrets.key");
        if (!File.Exists(keyPath) || !CryptographicOperations.FixedTimeEquals(StateFiles.ReadBounded(directory, "secrets.key", 32), key))
            throw new HubError("The Media Hub encryption key is missing or changed. Restore the matching backup; no key was replaced.");
    }
    private string Signature(HubSettings value) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("MediaHub.Settings.v3\n" + JsonSerializer.Serialize(value with { Integrity = "" }, Json))));
    private HubSettings Load(string? file = null, bool allowUnsigned = false)
    {
        CheckKey(); StateFiles.Check(directory); StateFiles.Check(file ?? path);
        if (!File.Exists(file ?? path))
        {
            if (file is not null || settings?.Revision > 0 || File.Exists(StateFiles.In(directory, "settings.previous.json")))
                throw new HubError("Retained Media Hub settings are missing. Restore the matching backup; no defaults were saved.");
            return new();
        }
        var text = Encoding.UTF8.GetString(StateFiles.ReadBounded(directory, Path.GetFileName(file ?? path), 65536)).TrimStart('\uFEFF');
        SafeJson.Validate(Encoding.UTF8.GetBytes(text), 65536);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new HubError("Invalid plugin settings.");
        var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        if (fields.Count != fields.Distinct(StringComparer.Ordinal).Count() ||
            new[] { "Revision", "DiscordEnabled", "PublicJellyfinUrl", "AllowedGuilds", "SeerrUrl", "DiscordTokenEncrypted", "SeerrKeyEncrypted" }.Except(fields, StringComparer.Ordinal).Any())
            throw new HubError("Incomplete or ambiguous Media Hub settings. Retained state was not changed.");
        var value = JsonSerializer.Deserialize<HubSettings>(text, Json) ?? throw new HubError("Invalid plugin settings.");
        if (file is null && settings is not null && value.Revision < settings.Revision) throw new HubError("Media Hub settings revision moved backwards while running. Restore trusted state during stopped maintenance; no integration was activated.");
        if (value.FormatVersion is not (1 or 2 or 3) || value.Revision < 0 || value.AllowedGuilds is null || value.AllowedGuilds.Length > 100 ||
            value.PublicJellyfinUrl is null || value.SeerrUrl is null || value.DiscordTokenEncrypted is null || value.SeerrKeyEncrypted is null ||
            value.AllowedGuilds.Any(id => !ulong.TryParse(id, out var snowflake) || snowflake == 0))
            throw new HubError("Unsupported or invalid Media Hub settings. Retained state was not changed.");
        if (value.FormatVersion == 3 && (value.Integrity is null || value.Integrity.Length != 64 || !value.Integrity.All(Uri.IsHexDigit) ||
            !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(value.Integrity), Convert.FromHexString(Signature(value)))))
            throw new HubError("Media Hub settings integrity verification failed. Restore a trusted backup; no integrations were activated.");
        if (value.FormatVersion < 3 && !allowUnsigned) throw new HubError("Older unsigned Media Hub settings require elevated review and an explicit save before any integration is used. Retained settings were not changed.");
        _ = Url(value.PublicJellyfinUrl, true); _ = Url(value.SeerrUrl, false);
        new DeletionRules(value.RecentActivityDays, value.DeletionDelayDays).Validate();
        ValidateFeatures(value.Features);
        var botKey = Unprotect(value.DiscordTokenEncrypted); var seerrKey = Unprotect(value.SeerrKeyEncrypted);
        if (botKey.Length > 0) ServiceAddress.Key(botKey); if (seerrKey.Length > 0) ServiceAddress.Key(seerrKey);
        if (value.DiscordEnabled && (value.PublicJellyfinUrl.Length == 0 || value.DiscordTokenEncrypted.Length == 0 || value.AllowedGuilds.Length == 0))
            throw new HubError("Incomplete Media Hub Discord settings. Retained state was not changed.");
        return value;
    }
    public void Update(SettingsUpdate update)
    {
        if (update is null || update.AllowedGuilds is null) throw new HubError("Specify the integration settings and allowed servers.");
        lock (gate)
        {
            using var lease = StateFiles.Lease(directory, "settings.lock");
            settings = ReviewOnly(Load(allowUnsigned: true)); // Only this explicit, elevated save may approve unsigned old settings.
            if (update.Revision != settings.Revision) throw new HubError("Settings changed in another window. Reload before saving.");
            if (update.DiscordToken?.Length > 4096 || update.SeerrKey?.Length > 4096) throw new HubError("Media Hub integration secrets must not exceed 4096 characters.");
            if (update.AllowedGuilds.Length > 100 || update.AllowedGuilds.Any(id => !ulong.TryParse(id, out var snowflake) || snowflake == 0)) throw new HubError("Invalid Discord server IDs.");
            var next = new HubSettings { Revision = checked(settings.Revision + 1), DiscordEnabled = update.DiscordEnabled,
                AutomaticDeletion = update.AutomaticDeletion ?? settings.AutomaticDeletion,
                Features = update.Features is not { } f ? settings.Features : new ExtendedSettings { Lidarr = Extra(f.Lidarr, settings.Features.Lidarr), Readarr = Extra(f.Readarr, settings.Features.Readarr),
                    Operators = f.Operators is null ? throw new HubError("Specify operator IDs.") : [.. f.Operators], SeerrOperators = f.SeerrOperators, MessageContent = f.MessageContent, MembersIntent = f.MembersIntent,
                    InboxChannels = f.InboxChannels is null ? [] : [.. f.InboxChannels], MessageRetentionDays = f.MessageRetentionDays, MaxRecipients = f.MaxRecipients,
                    DiscordApplicationId = f.DiscordApplicationId ?? settings.Features.DiscordApplicationId, DiscordClientSecretEncrypted = f.DiscordClientSecret is null ? settings.Features.DiscordClientSecretEncrypted : Protect(f.DiscordClientSecret),
                    ActivityEnabled = f.ActivityEnabled ?? settings.Features.ActivityEnabled, OAuthPkce = f.OAuthPkce ?? settings.Features.OAuthPkce,
                    NotificationHookKeyEncrypted = f.NotificationHookKey is null ? settings.Features.NotificationHookKeyEncrypted : f.NotificationHookKey.Length == 0 ? null : Protect(f.NotificationHookKey),
                    ArrDestinations = f.ArrDestinations is null ? [.. settings.Features.ArrDestinations] : f.ArrDestinations.Select(d => d is null ? throw new HubError("Invalid approved Arr destination.") : d with { Url = ServiceAddress.Url(d.Url) }).ToArray() },
                RecentActivityDays = update.RecentActivityDays ?? settings.RecentActivityDays,
                DeletionDelayDays = update.DeletionDelayDays ?? settings.DeletionDelayDays,
                PublicJellyfinUrl = Url(update.PublicJellyfinUrl, true), SeerrUrl = Url(update.SeerrUrl, false), AllowedGuilds = [.. update.AllowedGuilds],
                DiscordTokenEncrypted = update.DiscordToken is null ? settings.DiscordTokenEncrypted : Protect(update.DiscordToken),
                SeerrKeyEncrypted = update.SeerrKey is null ? settings.SeerrKeyEncrypted : Protect(update.SeerrKey) };
            new DeletionRules(next.RecentActivityDays, next.DeletionDelayDays).Validate();
            ValidateFeatures(next.Features);
            if (update.DiscordToken is { Length: > 0 } botKey) ServiceAddress.Key(botKey); if (update.SeerrKey is { Length: > 0 } seerrKey) ServiceAddress.Key(seerrKey);
            if (next.DiscordEnabled && (next.PublicJellyfinUrl.Length == 0 || next.DiscordTokenEncrypted.Length == 0 || next.AllowedGuilds.Length == 0))
                throw new HubError("Enabling Discord requires a token, public Jellyfin HTTPS URL, and allowed Discord server IDs.");
            next = next with { Integrity = Signature(next) };
            var previous = StateFiles.In(directory, "settings.previous.json");
            if (File.Exists(previous)) _ = Load(previous, allowUnsigned: true);
            StateFiles.Publish(directory, "settings.json", JsonSerializer.SerializeToUtf8Bytes(next), "settings.previous.json", checkpoint);
            settings = next;
        }
    }
    public string Protect(string value, string? purpose = null)
    {
        CheckKey();
        if (value.Length == 0) return "";
        if (value.Length > 16384) throw new HubError("Media Hub credential exceeds its supported size. No credential was saved.");
        var nonce = RandomNumberGenerator.GetBytes(12); var plaintext = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[plaintext.Length]; var tag = new byte[16];
        using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plaintext, encrypted, tag, Encoding.UTF8.GetBytes(purpose ?? ""));
        return Convert.ToBase64String([.. nonce, .. tag, .. encrypted]);
    }
    public string DiscordToken()
    {
        return Unprotect(Current.DiscordTokenEncrypted);
    }
    public string Unprotect(string secret, string? purpose = null)
    {
        CheckKey();
        if (secret is null || secret.Length > 90000) throw new HubError("Invalid or oversized encrypted Media Hub data.");
        if (secret.Length == 0) return "";
        var bytes = Convert.FromBase64String(secret);
        if (bytes.Length < 28) throw new HubError("Invalid encrypted Media Hub credential. Restore a consistent backup; no credential was reset.");
        var plaintext = new byte[bytes.Length - 28];
        using var aes = new AesGcm(key, 16); aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Encoding.UTF8.GetBytes(purpose ?? ""));
        return Encoding.UTF8.GetString(plaintext);
    }
    internal static string LinkPurpose(string discord, Guid user, string device) => "MediaHub.Link.v1|" + discord + "|" + user.ToString("N") + "|" + device;
    internal string ProtectLink(string token, string discord, Guid user, string device) => Protect(token, LinkPurpose(discord, user, device));
    internal string Authenticate(string purpose, string value)
    {
        CheckKey(); if (value.Length > 1048576) throw new HubError("Authorization record exceeds its safe size.");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(purpose + "\n" + value)));
    }
    internal bool Authenticated(string purpose, string value, string signature) => signature is { Length: 64 } && signature.All(Uri.IsHexDigit) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Authenticate(purpose, value)));
    public string WebUrl()
    {
        // Plugin Pages adds ApiClient's configured base path to pageUrl.
        return Current.PublicJellyfinUrl + "/web/index.html#/userpluginsettings.html?pageUrl=" + Uri.EscapeDataString("/web/ConfigurationPage?name=mediahub");
    }
}
