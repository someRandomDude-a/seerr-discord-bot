namespace Jellyfin.Plugin.MediaHub;

internal static class ActivityRequests
{
    internal sealed record PersonalUpdate(long Revision, PersonalPreferences Preferences);
    internal sealed record RequestSelection(string Kind, int ExternalId, bool Is4k = false, int[]? Seasons = null);
    internal sealed record Interaction(MediaInteraction Action);
    internal sealed record WatchlistRemove(long Revision);
    internal sealed record BellUpdate(long Revision, bool Subscribed);
    internal sealed record ExternalWatchUpdate(long Revision, bool? Subscribed, bool SaveOnly = false);
    internal sealed record SyncAcceptance(string Token);
    internal sealed record ExtraSelection(string Kind, string ForeignId, string? Edition = null);
    internal sealed record Proposal(int RequestId, Guid Item);
    internal sealed record NativeProposal(Guid Item);
    internal sealed record ConsentResponse(bool Approve);
    internal sealed record Compose(string Mode, string Content, MessageTarget[] Targets, string[] Uploads, string? RequestId = null, bool Members = false);
}
