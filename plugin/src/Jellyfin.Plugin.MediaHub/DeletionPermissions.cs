using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaHub;

public enum DeletionScope { Denied, OwnRequests, AnyAccessibleItem }
public sealed record DeletionPermissionOverride(Guid User, [property: JsonConverter(typeof(DeletionScopeJsonConverter))] DeletionScope Scope);
public sealed record DeletionPermissionSettings([property: JsonConverter(typeof(DeletionScopeJsonConverter))] DeletionScope Default = DeletionScope.OwnRequests, DeletionPermissionOverride[]? Users = null);
public sealed class DeletionScopeJsonConverter : JsonConverter<DeletionScope>
{
    public override DeletionScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((DeletionScope)number)) return (DeletionScope)number;
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<DeletionScope>(reader.GetString(), out var named) && Enum.IsDefined(named)) return named;
        throw new JsonException("Unknown deletion permission.");
    }
    public override void Write(Utf8JsonWriter writer, DeletionScope value, JsonSerializerOptions options) => writer.WriteNumberValue((int)value);
}
public sealed record NativeDeletionSource(Guid User, Guid Item);
public sealed record NativeDeletionSelection(Guid Item, string Kind, int Tmdb, int Provider, string Path);
public interface INativeDeletionResolver
{
    Task<VerifiedDeletionTarget> Discover(User actor, Guid item, CancellationToken cancellation);
}
public sealed class DeletionPermissions(SettingsService settings)
{
    internal static void Validate(DeletionPermissionSettings? value)
    {
        if (value is null) return;
        if (!Enum.IsDefined(value.Default) || value.Users is { Length: > 500 } || value.Users is { } rows &&
            (rows.Any(row => row is null || row.User == Guid.Empty || !Enum.IsDefined(row.Scope)) || rows.Select(row => row.User).Distinct().Count() != rows.Length))
            throw new HubError("Specify valid unique deletion permissions (maximum 500 account overrides).");
    }
    public DeletionScope Scope(User actor)
    {
        if (actor.Id == Guid.Empty || actor.HasPermission(PermissionKind.IsDisabled)) throw new HubError("An active native account is required.");
        var policy = settings.Current.DeletionPermissions;
        Validate(policy);
        return policy?.Users?.SingleOrDefault(row => row.User == actor.Id)?.Scope ?? policy?.Default ?? DeletionScope.OwnRequests;
    }
    public void Require(User actor, bool native)
    {
        var scope = Scope(actor);
        if (scope == DeletionScope.Denied || native && scope != DeletionScope.AnyAccessibleItem)
            throw new HubError("Your current deletion-request permission does not allow this proposal.", "deletion_denied");
    }
}

/// <summary>Fresh native account, visibility and provider/path authority. Never accepts caller-supplied paths.</summary>
public sealed class NativeDeletionReader(IUserManager users, ILibraryManager library, DeletionPermissions permissions)
{
    public NativeDeletionSelection Read(Guid actor, Guid itemId)
    {
        var user = users.GetUserById(actor) ?? throw new HubError("Native account unavailable.");
        if (user.Id != actor) throw new HubError("Native account identity changed.");
        permissions.Require(user, native: true);
        var item = library.GetItemById(itemId);
        if (item is null || !item.IsVisibleStandalone(user) || MediaActivity.Root(item).Id != itemId) throw new HubError("Item unavailable.");
        var kind = item is MediaBrowser.Controller.Entities.Movies.Movie ? "movie" : item is MediaBrowser.Controller.Entities.TV.Series ? "tv" : throw new HubError("Verified deletion currently supports movies and entire series only.");
        if (!item.ProviderIds.TryGetValue("Tmdb", out var tmdb) || !int.TryParse(tmdb, out var tmdbId) || tmdbId <= 0 || string.IsNullOrWhiteSpace(item.Path)) throw new HubError("Native provider/path identity is incomplete.");
        var provider = tmdbId;
        if (kind == "tv" && (!item.ProviderIds.TryGetValue("Tvdb", out var tvdb) || !int.TryParse(tvdb, out provider) || provider <= 0)) throw new HubError("Native TV provider identity is incomplete.");
        return new(itemId, kind, tmdbId, provider, item.Path);
    }
}
