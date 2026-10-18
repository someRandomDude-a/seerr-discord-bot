using System.Text.Json;

namespace Jellyfin.Plugin.MediaHub;

public sealed record MediaControl(string Action, string Kind = "movie", int Id = 0, Guid? Native = null,
    int Page = 1, string Query = "", bool All = false, long Revision = 0, string? Token = null, string? ForeignId = null, string? Edition = null,
    int[]? Seasons = null, int Offset = 0, bool NativeOnly = false, string? Guild = null, string? Peer = null, string? Channel = null, bool Oldest = false);

/// <summary>Opaque, expiring controls bound to both identities and the exact message. One click invalidates the old view.</summary>
public sealed class DiscordControls(HubStore store, SettingsService settings)
{
    private static string Purpose(string id, string view, string discord, string user, string expires, string message) =>
        "MediaHub.DiscordControl.v1|" + JsonSerializer.Serialize(new { id, view, discord, user, expires, message });
    private MediaControl Read(string ciphertext, string purpose)
    {
        try { return SafeJson.Read<MediaControl>(settings.Unprotect(ciphertext, purpose), 8192); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException) { throw new HubError("Control integrity verification failed. Reopen the command."); }
    }
    public string Add(string view, string discord, Guid user, MediaControl payload)
    {
        var id = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"); var actor = user.ToString("N"); using var db = store.Open();
        using (var prune = HubStore.Command(db, "DELETE FROM discord_controls WHERE expires<$0", DateTimeOffset.UtcNow.ToString("O"))) prune.ExecuteNonQuery();
        using (var capacity = HubStore.Command(db, "SELECT COUNT(*) FROM discord_controls")) if ((long)capacity.ExecuteScalar()! >= 10000) throw new HubError("Too many active controls. Reopen the command later.");
        using var write = HubStore.Command(db, "INSERT INTO discord_controls VALUES($0,$1,$2,$3,NULL,$4,$5,'active')", id, view, discord, actor, settings.Protect(JsonSerializer.Serialize(payload), Purpose(id, view, discord, actor, expires, "")), expires);
        write.ExecuteNonQuery(); return "mediahub:control:" + id;
    }
    public void Bind(string view, ulong message)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); var rows = new List<(string Id, string Discord, string User, string Payload, string Expires)>();
        using (var read = HubStore.Command(db, "SELECT id,discord,user,payload,expires FROM discord_controls WHERE view_id=$0 AND message IS NULL AND state='active' LIMIT 101", view))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))); }
        if (rows.Count > 100) throw new HubError("Too many controls in one view.");
        foreach (var row in rows)
        {
            var payload = Read(row.Payload, Purpose(row.Id, view, row.Discord, row.User, row.Expires, ""));
            using var bind = HubStore.Command(db, "UPDATE discord_controls SET message=$0,payload=$2 WHERE id=$1 AND message IS NULL AND state='active'", message.ToString(), row.Id,
                settings.Protect(JsonSerializer.Serialize(payload), Purpose(row.Id, view, row.Discord, row.User, row.Expires, message.ToString()))); bind.Transaction = tx; bind.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public MediaControl Claim(string id, string discord, Guid user, ulong message)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var read = HubStore.Command(db, "SELECT view_id,payload,expires FROM discord_controls WHERE id=$0 AND discord=$1 AND user=$2 AND message=$3 AND state='active' AND expires>$4",
            id, discord, user.ToString("N"), message.ToString(), DateTimeOffset.UtcNow.ToString("O")); read.Transaction = tx;
        string view; MediaControl payload;
        using (var r = read.ExecuteReader()) { if (!r.Read()) throw new HubError("This control expired or belongs to another account. Reopen the command."); view = r.GetString(0); payload = Read(r.GetString(1), Purpose(id, view, discord, user.ToString("N"), r.GetString(2), message.ToString())); }
        using var invalidate = HubStore.Command(db, "UPDATE discord_controls SET state='used',payload='' WHERE view_id=$0 AND state='active'", view); invalidate.Transaction = tx; invalidate.ExecuteNonQuery(); tx.Commit();
        return payload;
    }
}
