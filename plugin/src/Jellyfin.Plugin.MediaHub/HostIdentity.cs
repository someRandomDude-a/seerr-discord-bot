using System.Security.Claims;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Use Jellyfin's own live token/session/user managers, not a second admin API key.</summary>
public sealed class HostIdentity(HubStore store, SettingsService settings, IUserManager users, ISessionManager sessions)
{
    public User Active(Guid userId)
    {
        var user = users.GetUserById(userId);
        if (userId == Guid.Empty || user is null || user.Id != userId || user.HasPermission(PermissionKind.IsDisabled)) throw new HubError("Jellyfin verification is required. Reapprove /hub-link in Jellyfin.");
        return user;
    }
    private async Task<SessionInfo> Verify(string token, string device, Guid expectedUser)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(device)) throw new HubError("A signed-in Jellyfin player session is required.");
            Active(expectedUser);
            var session = await sessions.GetSessionByAuthenticationToken(token, device, "127.0.0.1");
            if (session.UserId != expectedUser || session.DeviceId != device) throw new HubError("Jellyfin identity changed. Verify again.");
            return session;
        }
        catch (HubError) { throw; }
        catch { throw new HubError("Live Jellyfin verification is unavailable or expired. No private data can be shown. Reapprove /hub-link if access was revoked."); }
    }
    public async Task<SessionInfo> Browser(ClaimsPrincipal principal)
    {
        // These claim names are defined by Jellyfin 12 InternalClaimTypes.
        if (principal.Identity?.IsAuthenticated != true || !bool.TryParse(principal.FindFirstValue("Jellyfin-IsApiKey"), out var apiKey) || apiKey ||
            !Guid.TryParse(principal.FindFirstValue("Jellyfin-UserId"), out var userId) || userId == Guid.Empty)
            throw new HubError("Sign in with a Jellyfin user account, not an API key.");
        return await Verify(principal.FindFirstValue("Jellyfin-Token") ?? "", principal.FindFirstValue("Jellyfin-DeviceId") ?? "", userId);
    }
    public async Task<User> Discord(string discord)
    {
        var session = await DiscordSession(discord);
        return Active(session.UserId);
    }
    public async Task<SessionInfo> DiscordSession(string discord)
    {
        var binding = store.Credential(discord);
        try { return await Verify(settings.Unprotect(binding.Token, SettingsService.LinkPurpose(discord, binding.User, binding.Device)), binding.Device, binding.User); }
        catch (System.Security.Cryptography.CryptographicException) { throw new HubError("Stored link identity verification failed or needs upgrading. Explicitly reapprove your own /hub-link; no account was substituted."); }
    }
    public Task<User> Linked(Guid user) => Discord(store.DiscordFor(user));
    public void Approve(ClaimsPrincipal principal, SessionInfo session, string code, string discord)
    {
        if (!ulong.TryParse(discord, out var id) || id == 0) throw new HubError("Check the Discord user ID before approving.");
        var token = principal.FindFirstValue("Jellyfin-Token") ?? throw new HubError("Jellyfin user authentication required.");
        store.Approve(code, session.UserId, discord, settings.ProtectLink(token, discord, session.UserId, session.DeviceId), session.DeviceId);
    }
}
