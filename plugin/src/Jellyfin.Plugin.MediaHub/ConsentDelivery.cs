namespace Jellyfin.Plugin.MediaHub;

/// <summary>Personal opt-in, live identity checks and one durable send intent per notice.</summary>
public sealed class ConsentDelivery(HubStore store, SettingsService settings, HostIdentity identity, PersonalService personal, DeletionQueue queue)
{
    private string cursor = "";
    public async Task Deliver(Func<string, ConsentNotice, CancellationToken, Task> send, CancellationToken cancellation)
    {
        if (!settings.Current.DiscordEnabled) return;
        var users = new List<string>();
        using (var db = store.Open())
        using (var read = HubStore.Command(db, "SELECT DISTINCT user FROM deletion_notices WHERE user>$0 ORDER BY user LIMIT 25", cursor))
        using (var reader = read.ExecuteReader()) while (reader.Read()) users.Add(reader.GetString(0));
        if (users.Count == 0) { cursor = ""; return; }
        foreach (var id in users)
        {
            cancellation.ThrowIfCancellationRequested(); cursor = id;
            string discord; IReadOnlyList<ConsentNotice> notices;
            try
            {
                var user = await identity.Linked(Guid.Parse(id));
                var preferences = personal.State(user).Preferences;
                if (!preferences.DiscordNotifications || preferences.Muted) continue;
                discord = store.DiscordFor(user.Id); notices = queue.Notices(user);
            }
            catch { continue; } // Unavailable verification does not consume a delivery intent.
            foreach (var notice in notices)
            {
                if (!settings.Current.DiscordEnabled) return;
                ConsentNotice current;
                try
                {
                    var verified = await identity.Discord(discord);
                    var preferences = personal.State(verified).Preferences;
                    if (verified.Id.ToString("N") != id || !preferences.DiscordNotifications || preferences.Muted) break;
                    current = queue.Notices(verified).FirstOrDefault(n => n.Id == notice.Id)!;
                    if (current is null) continue;
                }
                catch { continue; }
                using (var db = store.Open())
                using (var claim = HubStore.Command(db, "INSERT OR IGNORE INTO consent_delivery VALUES($0,'sending',$1)", notice.Id, DateTimeOffset.UtcNow.ToString("O")))
                    if (claim.ExecuteNonQuery() != 1) continue;
                var state = "sent";
                try { await send(discord, current, cancellation); }
                catch { state = "uncertain"; }
                using (var db = store.Open())
                using (var result = HubStore.Command(db, "UPDATE consent_delivery SET state=$0,updated=$1 WHERE notice=$2", state, DateTimeOffset.UtcNow.ToString("O"), notice.Id)) result.ExecuteNonQuery();
                cancellation.ThrowIfCancellationRequested();
            }
        }
    }
}
