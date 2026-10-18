using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;

public sealed record LinkedActor(Guid User, string Session, string Device);
public sealed record OAuthConfiguration(string ApplicationId, string ClientSecret, bool ActivityEnabled, string[] AllowedGuilds);
public sealed record DiscordGrant(string AccessToken, string Discord, int ExpiresIn, string[] Scopes);
public sealed record GatewayFeatures(bool MessageContent, bool MembersIntent, int MaxRecipients, string DiscordApplicationId, string[] InboxChannels, bool ActivityEnabled = false, bool OAuthPkce = true);
public sealed record ActivityProfile(bool Linked, Guid? UserId, string? Name, string JellyfinUrl, ActivityTheme? Theme = null);
public sealed record GatewaySettings(int Revision, bool DiscordEnabled, string[] AllowedGuilds, string PublicJellyfinUrl, GatewayFeatures Features);
// Trusted in-process configuration capability only; never return this credential-bearing DTO to a browser.
public sealed record DiscordConfigurationSnapshot(int Revision, bool DiscordEnabled, string BotToken, string[] AllowedGuilds,
    string ApplicationId, string ClientSecret, bool ActivityEnabled, bool OAuthPkce, bool MessageContent, bool MembersIntent,
    string[] Operators, bool SeerrOperators, string[] InboxChannels, int MessageRetentionDays, int MaxRecipients);
public sealed record MediaControl(string Action, string Kind = "movie", int Id = 0, Guid? Native = null, int Page = 1, string Query = "", bool All = false, long Revision = 0, string? Token = null, string? ForeignId = null, string? Edition = null, int[]? Seasons = null, int Offset = 0, bool NativeOnly = false, string? Guild = null, string? Peer = null, string? Channel = null, bool Oldest = false);
public enum MediaInteraction { Details, Open, Request, Follow }
public sealed record ActivityReceipt(bool JellyfinUpdated, bool LocalRecorded = true);
public sealed record ActivityTheme(string Background, string Text, string Accent, string Surface);
public sealed record PersonalPreferences(bool JellyfinNotifications = true, bool DiscordNotifications = false, bool Muted = false, int? DeletionDelayDays = null, ActivityTheme? Theme = null, bool OwnRequestNotifications = true,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DeletionMessage = null);
public sealed record DeletionRules(int RecentActivityDays = 14, int DefaultDelayDays = 2);
public sealed record VisibleItem(Guid Id, string Title, string Kind);
public sealed record FollowedItem(Guid ItemId, string Title, string Kind, bool Subscribed);
public sealed record ExternalFollow(string Kind, int ExternalId, string Title, bool Subscribed, Guid? NativeId, IReadOnlyList<Guid>? NativeIds = null);
public sealed record ExtraWatch(string Kind, string ForeignId, string Title, bool Subscribed, Guid? NativeId);
public sealed record PersonalState(long Revision, PersonalPreferences Preferences, IReadOnlyList<FollowedItem> Watchlist, DeletionRules DeletionRules, int EffectiveDeletionDelayDays, IReadOnlyList<ExternalFollow>? ExternalWatchlist = null, ActivityReceipt? Activity = null, IReadOnlyList<ExtraWatch>? ExtraWatchlist = null, bool WatchlistReviewRequired = false);
public sealed record MediaCard(string Kind, int ExternalId, Guid? NativeId, string Title, string Overview, string Status, bool Available, bool Requestable, string? Poster, int? RequestId = null, bool Is4k = false, IReadOnlyList<int>? Seasons = null, ActivityReceipt? Activity = null, bool NativeOnly = false);
public sealed record MediaOpen(string Item, ActivityReceipt Activity);
public sealed record MediaEdition(Guid Id, string Title);
public sealed record MediaPage(IReadOnlyList<MediaCard> Items, int Page, int Pages);
public sealed record UserQuota(int? Days, int? Limit, int? Used, int? Remaining, bool? Restricted);
public sealed record RequestPermissions(bool Movies, bool Series, bool Movie4k, bool Series4k, UserQuota MovieQuota, UserQuota SeriesQuota);
public sealed record RequestPlan(string Id, string Title, DateTimeOffset Expires);
public sealed record RequestReceipt(string Id, string State, int? RequestId, ActivityReceipt? Activity = null);
public sealed record ExtraCard(string Kind, string ForeignId, string Title, string Overview, string Status, bool Requestable, Guid? NativeId, IReadOnlyList<string> Editions, ActivityReceipt? Activity = null);
public sealed record ExtraPage(IReadOnlyList<ExtraCard> Items, int Page, int Pages);
public sealed record MediaKey(string Kind, int Id);
public sealed record WatchSyncResult(bool Complete, int Pending);
public sealed record WatchSyncIssue(string Kind, int ExternalId, string Title, string State, bool Desired, long Revision);
public sealed record WatchSyncReview(string Token, string Title, bool LocalMembership, bool RemoteMembership, DateTimeOffset Expires);
public sealed record LegacyWatchReview(string Token, IReadOnlyList<string> Titles, DateTimeOffset Expires);
public sealed record QueueItem(string Id, Guid Item, string Title, string State, DateTimeOffset? DueAt, long Revision, string Owner, bool CanUndo, bool AutomaticDeletion = false);
public sealed record ConsentNotice(string Id, string Proposal, Guid Item, string Title, string Message);
public sealed record PersonalDeletionStatus(string Proposal, Guid Item, string Title, string State, DateTimeOffset? DueAt,
    bool AutomaticDeletion, bool Yours, bool CanUndo, string RequiredAction, DateTimeOffset? YourLastActivity,
    bool ActivityVerified, string YourConsent, string? Notice, int ActivityWindowDays, int DelayDays);
public sealed record OwnedRequest(int RequestId, bool Is4k);
public sealed record DeletionPermission(string Scope, bool NativeTargetVerificationRequired);
public sealed record HubNotification(string Id, Guid? Item, string Title, string Status, DateTimeOffset Created, bool Seen);
public sealed record NotificationDiagnostics(DateTimeOffset? LastCompletedScan, int NextOwnRequestPage, bool JellyfinFeed, bool DiscordOptIn, bool Muted, bool OwnRequests, IReadOnlyDictionary<string, long> Deliveries);
public sealed record PlayerDestination(string Id, string Name, string Client);
public sealed record PlaybackPreview(string Id, string Title, string Device, DateTimeOffset Expires);
public sealed record PlaybackReceipt(string Id, string State, bool? ObservedPlayingItem);
public sealed record PlaybackHistory(PlaybackPreview Preview, PlaybackReceipt Receipt);
public sealed record SeerrViewer(int Id, long Permissions, string Origin);
public sealed record ServiceSpace(string Service, int Instance, bool Verified, long? Free, long? Total);
public sealed record OperatorActor(Guid User, string Discord);
public sealed record MessageTarget(string Kind, string Id, string? Guild = null);
public sealed record MessageDestination(MessageTarget Target, string Label);
public sealed record MessageUpload(string Id, string Filename, long Size, string Mime);
public sealed record SendPreview(string Id, string Mode, string Content, IReadOnlyList<MessageDestination> Destinations, IReadOnlyList<MessageUpload> Files, DateTimeOffset Expires, string State);
public sealed record MessageDelivery(string Id, string State, string? MessageId, string Destination);
public sealed record SendReceipt(string Id, string Mode, string Content, IReadOnlyList<MessageDelivery> Deliveries, DateTimeOffset Created);
public sealed record MessageView(string Id, string Author, string AuthorName, string? Guild, string? GuildName, string Channel, string ChannelName, string Content, JsonNode Rich, DateTimeOffset Created, DateTimeOffset? Edited, bool Deleted, string Direction, string? Peer, string? DeliveryState);
public sealed record InboxPage(IReadOnlyList<MessageView> Messages, int Page, bool HasNext);
public sealed record MessageBlob(byte[] Bytes, string Filename, string Mime);
