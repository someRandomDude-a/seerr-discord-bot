namespace Jellyfin.Plugin.MediaHub;

public sealed record OperatorActor(Guid User, string Discord);

/// <summary>Messaging operation authority is distinct from native administration and media ownership.</summary>
public sealed class OperatorAuthority(HostIdentity identity, HubStore store, SettingsService settings, SeerrBridge bridge)
{
    public async Task<OperatorActor> Require(Guid user, CancellationToken cancellation)
    {
        Jellyfin.Database.Implementations.Entities.User verified;
        try { verified = await identity.Linked(user); }
        catch (HubError) { throw new HubError("Live operator identity verification is required.", "operator_denied"); }
        if (verified.Id != user) throw new HubError("Linked identity changed.", "operator_denied");
        var discord = store.DiscordFor(user); var f = settings.Current.Features;
        if (f.Operators.Contains(discord, StringComparer.Ordinal)) return new(user, discord);
        if (f.SeerrOperators)
        { using var scope = await bridge.Open(verified, cancellation); if (scope.Can(2)) return new(user, discord); }
        throw new HubError("Operator access is required. Jellyfin administration alone does not grant messaging access.", "operator_denied");
    }
    public async Task Check(OperatorActor actor, CancellationToken cancellation)
    { if (await Require(actor.User, cancellation) != actor) throw new HubError("Operator identity changed.", "operator_denied"); }
}
