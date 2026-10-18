using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.MediaHub;

public sealed record LinkStart(string AuthorizeUrl, string Discord, string Jellyfin, DateTimeOffset Expires,
    [property: JsonIgnore] string State, [property: JsonIgnore] string Cookie);
public sealed record LinkStatus(string State, string Discord, string Jellyfin);

/// <summary>Native browser proof, one-use OAuth state/PKCE and a same-browser HttpOnly callback cookie. GET never links an account.</summary>
public sealed class LinkFlow(HubStore store, SettingsService settings, HostIdentity identity, SeerrBridge seerr, IDiscordOAuth oauth)
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Browser(ClaimsPrincipal principal, SessionInfo session) => Hash(string.Join('|', principal.FindFirstValue("Jellyfin-Token") ?? "", session.UserId, session.DeviceId));
    internal string Pin() { var s = settings.Current; return Hash(string.Join('|', s.PublicJellyfinUrl, s.Features.DiscordApplicationId, s.Features.DiscordClientSecretEncrypted, s.Features.OAuthPkce)); }
    public string Redirect => settings.Current.PublicJellyfinUrl.TrimEnd('/') + "/MediaHub/Link/OAuth/Callback";
    public string HandoffUrl(string handoff) => settings.Current.PublicJellyfinUrl.TrimEnd('/') + "/MediaHub/Link?handoff=" + Uri.EscapeDataString(handoff);
    public string WebUrl(string handoff) => settings.WebUrl() + "&hubLink=" + Uri.EscapeDataString(handoff);
    internal static string CookieName(string state) => "MediaHubLink-" + Hash(state)[..16];
    private static string ProofPurpose(string state, string handoff, string user, string browser, string pin, string expires, string cookie, string status, string discord) =>
        "MediaHub.OAuthProof.v1|" + System.Text.Json.JsonSerializer.Serialize(new { state, handoff, user, browser, pin, expires, cookie, status, discord });
    private string ReadProof(string ciphertext, string purpose)
    {
        try { return settings.Unprotect(ciphertext, purpose); }
        catch (Exception error) when (error is CryptographicException or FormatException) { throw new HubError("Link proof integrity could not be verified. Verify both identities again."); }
    }
    public async Task<LinkStart> Begin(ClaimsPrincipal principal, string handoff, CancellationToken cancellation)
    {
        if (handoff.Length != 48) throw new HubError("Invalid handoff."); var discord = store.PendingDiscord(handoff); var session = await identity.Browser(principal); var user = identity.Active(session.UserId);
        using var scope = await seerr.Open(user, cancellation); var s = settings.Current;
        if (s.PublicJellyfinUrl.Length == 0 || s.Features.DiscordApplicationId.Length == 0) throw new HubError("OAuth linking is not configured. Use explicit code approval.");
        var state = HubStore.Nonce(); var verifier = HubStore.Nonce(); var cookie = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var browser = Browser(principal, session); var pin = Pin(); var expiry = expires.ToString("O");
        var purpose = ProofPurpose(Hash(state), Hash(handoff), user.Id.ToString("N"), browser, pin, expiry, Hash(cookie), "awaiting", "");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var prune = HubStore.Command(db, "DELETE FROM link_oauth WHERE expires<=$0 OR (handoff_hash=$1 AND user=$2 AND browser=$3)", DateTimeOffset.UtcNow.ToString("O"), Hash(handoff), user.Id.ToString("N"), Browser(principal, session)); prune.Transaction = tx; prune.ExecuteNonQuery();
        using var capacity = HubStore.Command(db, "SELECT COUNT(*) FROM link_oauth"); capacity.Transaction = tx; if ((long)capacity.ExecuteScalar()! >= 1000) throw new HubError("Too many pending links. Try later.");
        using var insert = HubStore.Command(db, "INSERT INTO link_oauth VALUES($0,$1,$2,$3,$4,$5,$6,$7,'awaiting',NULL,$8)", Hash(state), Hash(handoff), settings.Protect(handoff, purpose + "|handoff"), user.Id.ToString("N"), browser, settings.Protect(verifier, purpose + "|verifier"), pin, expiry, Hash(cookie)); insert.Transaction = tx; insert.ExecuteNonQuery(); tx.Commit();
        var url = "https://discord.com/oauth2/authorize?response_type=code&client_id=" + Uri.EscapeDataString(s.Features.DiscordApplicationId) + "&scope=identify&redirect_uri=" + Uri.EscapeDataString(Redirect) + "&state=" + state;
        if (s.Features.OAuthPkce) url += "&code_challenge_method=S256&code_challenge=" + Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new(url, discord, user.Username, expires, state, cookie);
    }
    public async Task<string> Callback(string state, string code, string cookie, CancellationToken cancellation)
    {
        if (state.Length != 48 || code.Length is < 1 or > 1024 || cookie.Length != 48) throw new HubError("OAuth browser verification failed. Start again.");
        string handoff, verifier, pin, user, browser, expiry, cookieHash, handoffHash;
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            using var read = HubStore.Command(db, "SELECT handoff,verifier,pin,cookie,user,browser,expires,handoff_hash FROM link_oauth WHERE state=$0 AND status='awaiting' AND expires>$1", Hash(state), DateTimeOffset.UtcNow.ToString("O")); read.Transaction = tx; using var r = read.ExecuteReader();
            if (!r.Read() || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(r.GetString(3)), Encoding.ASCII.GetBytes(Hash(cookie)))) throw new HubError("OAuth callback came from a different browser or expired.");
            pin = r.GetString(2); cookieHash = r.GetString(3); user = r.GetString(4); browser = r.GetString(5); expiry = r.GetString(6); handoffHash = r.GetString(7);
            var purpose = ProofPurpose(Hash(state), handoffHash, user, browser, pin, expiry, cookieHash, "awaiting", "");
            handoff = ReadProof(r.GetString(0), purpose + "|handoff"); verifier = ReadProof(r.GetString(1), purpose + "|verifier"); r.Close(); if (pin != Pin() || handoffHash != Hash(handoff)) throw new HubError("OAuth configuration or handoff changed. Start again.");
            using var claim = HubStore.Command(db, "UPDATE link_oauth SET status='exchanging' WHERE state=$0 AND status='awaiting'", Hash(state)); claim.Transaction = tx; if (claim.ExecuteNonQuery() != 1) throw new HubError("OAuth state was already used."); tx.Commit();
        }
        DiscordGrant? grant = null;
        try
        {
            var expected = store.PendingDiscord(handoff); var result = grant = await oauth.Exchange(code, Redirect, settings.Current.Features.OAuthPkce ? verifier : null, cancellation);
            if (result.Discord != expected || pin != Pin()) throw new HubError("This Discord browser identity does not match the person who requested the link.");
            var purpose = ProofPurpose(Hash(state), handoffHash, user, browser, pin, expiry, cookieHash, "proved", result.Discord);
            using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE link_oauth SET status='proved',discord=$0,verifier='',handoff=$3 WHERE state=$1 AND status='exchanging' AND expires>$2", result.Discord, Hash(state), DateTimeOffset.UtcNow.ToString("O"), settings.Protect(handoff, purpose + "|handoff"));
            if (update.ExecuteNonQuery() != 1) throw new HubError("Link proof expired. Start again."); return handoff;
        }
        catch { using var db = store.Open(); using var failed = HubStore.Command(db, "UPDATE link_oauth SET status='failed',verifier='' WHERE state=$0 AND status='exchanging'", Hash(state)); failed.ExecuteNonQuery(); throw; }
        finally { if (grant is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); try { await oauth.Revoke(grant.AccessToken, timeout.Token); } catch { /* No uncertain token-exchange or revocation replay. */ } } }
    }
    private (string State, string Status, string Discord, string Pin, string UsedPurpose)? Pending(ClaimsPrincipal principal, SessionInfo session, string handoff)
    {
        var browser = Browser(principal, session); var user = session.UserId.ToString("N");
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT state,status,discord,pin,expires,cookie,handoff FROM link_oauth WHERE handoff_hash=$0 AND user=$1 AND browser=$2 AND expires>$3 ORDER BY expires DESC LIMIT 1", Hash(handoff), user, browser, DateTimeOffset.UtcNow.ToString("O")); using var r = read.ExecuteReader();
        if (!r.Read()) return null;
        var state = r.GetString(0); var status = r.GetString(1); var discord = r.IsDBNull(2) ? store.PendingDiscord(handoff) : r.GetString(2); var pin = r.GetString(3); var expires = r.GetString(4); var cookie = r.GetString(5);
        if (status is "proved" or "used")
            if (ReadProof(r.GetString(6), ProofPurpose(state, Hash(handoff), user, browser, pin, expires, cookie, status, discord) + "|handoff") != handoff) throw new HubError("Link proof changed. Verify both identities again.");
        return (state, status, discord, pin, ProofPurpose(state, Hash(handoff), user, browser, pin, expires, cookie, "used", discord));
    }
    public async Task<LinkStatus> Status(ClaimsPrincipal principal, string handoff)
    {
        var session = await identity.Browser(principal); var pending = Pending(principal, session, handoff);
        return new(pending?.Status ?? "new", pending?.Discord ?? store.PendingDiscord(handoff), identity.Active(session.UserId).Username);
    }
    public async Task Complete(ClaimsPrincipal principal, string handoff, CancellationToken cancellation)
    {
        var session = await identity.Browser(principal); var pending = Pending(principal, session, handoff) ?? throw new HubError("Verify both browser identities first.");
        if (pending.Pin != Pin() || pending.Status is not ("proved" or "used")) throw new HubError("Verify both browser identities again.");
        using var scope = await seerr.Open(identity.Active(session.UserId), cancellation); await identity.Browser(principal); await scope.Verify(cancellation);
        if (pending.Pin != Pin()) throw new HubError("OAuth configuration changed. Verify both browser identities again.");
        if (pending.Status == "used") { if (store.LinkedUser(pending.Discord) != session.UserId) throw new HubError("Link identity changed."); return; }
        var token = principal.FindFirstValue("Jellyfin-Token") ?? throw new HubError("Native authentication is required.");
        store.Approve(handoff, session.UserId, pending.Discord, settings.ProtectLink(token, pending.Discord, session.UserId, session.DeviceId), session.DeviceId, pending.State, Browser(principal, session), settings.Protect(handoff, pending.UsedPurpose + "|handoff"));
    }
}
