using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record ActivityAuthentication(string Session, string AccessToken, string Discord);
public sealed record ActivityProfile(bool Linked, Guid? UserId, string? Name, string JellyfinUrl, ActivityTheme? Theme = null);
public sealed record ActivityIdentity(string Discord, User User);

/// <summary>Short-lived memory-only Discord OAuth sessions. SDK-supplied user IDs and native API keys are never identity proof.</summary>
public sealed class ActivitySessions(SettingsService settings, HubStore store, HostIdentity native, IDiscordOAuth oauth, TimeProvider? time = null)
{
    private sealed class Session(string discord, string access, string guild, string pin, DateTimeOffset expires)
    {
        public readonly string Discord = discord, Access = access, Guild = guild, Pin = pin;
        public readonly DateTimeOffset Expires = expires;
        public Guid? Native;
        public DateTimeOffset Window;
        public int Requests;
    }
    private readonly ConcurrentDictionary<string, (string Peer, DateTimeOffset Expires)> challenges = new();
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Count)> limits = new();
    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();
    private string Pin() { var s = settings.Current; return LinkFlow.Hash(string.Join('|', s.Features.DiscordApplicationId, s.Features.DiscordClientSecretEncrypted, s.Features.ActivityEnabled, s.PublicJellyfinUrl)); }
    private void Enabled() { var s = settings.Current; if (!s.Features.ActivityEnabled || s.Features.DiscordApplicationId.Length == 0 || s.PublicJellyfinUrl.Length == 0) throw new HubError("Activity is disabled.", "activity_denied"); }
    private void Limit(string peer)
    {
        var now = Now;
        foreach (var old in limits.Where(p => p.Value.Start < now.AddMinutes(-1)).Take(100)) limits.TryRemove(old.Key, out _);
        if (limits.Count >= 10000 && !limits.ContainsKey(peer)) throw new HubError("Authentication capacity reached.");
        var value = limits.AddOrUpdate(peer, (now, 1), (_, previous) => previous.Start < now.AddMinutes(-1) ? (now, 1) : (previous.Start, previous.Count + 1));
        if (value.Count > 20) throw new HubError("Too many authentication attempts. Wait a minute.");
    }
    public string Challenge(string peer)
    {
        Enabled(); Limit(peer); foreach (var old in challenges.Where(p => p.Value.Expires <= Now)) challenges.TryRemove(old.Key, out _);
        if (challenges.Count >= 1000) throw new HubError("Too many authentication attempts."); var token = HubStore.Nonce(); challenges[LinkFlow.Hash(token)] = (peer, Now.AddMinutes(1)); return token;
    }
    public async Task<ActivityAuthentication> Authenticate(string challenge, string code, string guild, string peer, CancellationToken cancellation)
    {
        Enabled(); Limit(peer);
        if (challenge.Length != 48 || !challenges.TryRemove(LinkFlow.Hash(challenge), out var intent) || intent.Peer != peer || intent.Expires <= Now) throw new HubError("Authentication challenge expired or was already used.", "activity_denied");
        var pin = Pin(); var grant = await oauth.Exchange(code, null, null, cancellation);
        var verified = await oauth.Verify(grant.AccessToken, guild, true, cancellation); if (verified != grant.Discord || pin != Pin()) throw new HubError("Discord identity or configuration changed.", "activity_denied");
        foreach (var old in sessions.Where(p => p.Value.Expires <= Now)) sessions.TryRemove(old.Key, out _);
        if (sessions.Count >= 1000) throw new HubError("Too many Activity sessions.");
        var token = HubStore.Nonce(); sessions[LinkFlow.Hash(token)] = new(grant.Discord, grant.AccessToken, guild, pin, Now.AddSeconds(Math.Clamp(grant.ExpiresIn, 1, 900)));
        return new(token, grant.AccessToken, grant.Discord); // Discord bearer is required by SDK authenticate; no Jellyfin credential is returned.
    }
    private async Task<Session> SessionFor(string token, CancellationToken cancellation)
    {
        Enabled(); if (token.Length != 48 || !sessions.TryGetValue(LinkFlow.Hash(token), out var session) || session.Expires <= Now || session.Pin != Pin()) throw new HubError("Activity session expired. Reopen Activity.", "activity_denied");
        lock (session) { if (session.Window < Now.AddMinutes(-1)) { session.Window = Now; session.Requests = 0; } if (++session.Requests > 120) throw new HubError("Slow down and try again shortly."); }
        try { if (await oauth.Verify(session.Access, session.Guild, true, cancellation) != session.Discord) throw new HubError("Discord verification changed.", "activity_denied"); }
        catch (HubError) { throw new HubError("Current Discord identity/server verification is required.", "activity_denied"); }
        Current(token, session);
        return session;
    }
    private void Current(string token, Session session)
    { if (!sessions.TryGetValue(LinkFlow.Hash(token), out var current) || !ReferenceEquals(current, session) || session.Expires <= Now || session.Pin != Pin()) throw new HubError("Activity session changed. Reopen Activity.", "activity_denied"); }
    private bool Linked(string discord)
    { using var db = store.Open(); using var read = HubStore.Command(db, "SELECT 1 FROM links WHERE discord=$0", discord); return read.ExecuteScalar() is not null; }
    private async Task<User> Native(Session session)
    {
        User user; try { user = await native.Discord(session.Discord); } catch (HubError) { throw new HubError("Native Jellyfin verification is required again.", "activity_denied"); }
        lock (session) { if (session.Native is Guid expected && expected != user.Id) throw new HubError("Linked Jellyfin identity changed. Reopen Activity.", "activity_denied"); session.Native = user.Id; }
        return user;
    }
    public async Task<ActivityIdentity> Require(string token, CancellationToken cancellation)
    { var session = await SessionFor(token, cancellation); var user = await Native(session); Current(token, session); return new(session.Discord, user); }
    public async Task<ActivityProfile> Profile(string token, CancellationToken cancellation)
    {
        var session = await SessionFor(token, cancellation); if (!Linked(session.Discord)) return new(false, null, null, settings.Current.PublicJellyfinUrl);
        var user = await Native(session); using var db = store.Open(); using var read = HubStore.Command(db, "SELECT preferences FROM personal WHERE user=$0", user.Id.ToString("N"));
        var text = read.ExecuteScalar() as string; var preferences = text is null ? null : SafeJson.Read<PersonalPreferences>(text, 4096); if (preferences is not null) PersonalService.Validate(preferences);
        Current(token, session); return new(true, user.Id, user.Username, settings.Current.PublicJellyfinUrl, preferences?.Theme);
    }
    public async Task<string> Link(string token, CancellationToken cancellation)
    { var session = await SessionFor(token, cancellation); return store.BeginLink(session.Discord); }
    public void Logout(string token) { if (token.Length == 48) sessions.TryRemove(LinkFlow.Hash(token), out _); }
}
