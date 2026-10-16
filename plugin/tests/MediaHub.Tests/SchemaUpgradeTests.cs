using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-schema-upgrade-" + Guid.NewGuid());
    [Theory]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void NativePreviewUpgradePreservesQueueSettingsAndUncertainOperations(int version)
    {
        var store = new HubStore(directory); var settings = new SettingsService(store); var proof = settings.Protect("pinned-target");
        using (var db = store.Open())
        {
            using (var seed = HubStore.Command(db, "INSERT INTO personal VALUES('viewer',7,$0); INSERT INTO deletion_queue VALUES('proposal','owner','item',22,'pin','uncertain',NULL,NULL,9,$1,2)", "{\"Muted\":true}", DateTimeOffset.UtcNow.ToString("O"))) seed.ExecuteNonQuery();
            if (version >= 5) using (var run = HubStore.Command(db, "INSERT INTO deletion_runs VALUES('proposal',$0,'files_deleted',$1)", proof, DateTimeOffset.UtcNow.ToString("O"))) run.ExecuteNonQuery();
            if (version >= 6) using (var request = HubStore.Command(db, "INSERT INTO request_operations VALUES('request','viewer','https://seerr',7,'movie',123,'{}','sending',NULL,$0)", DateTimeOffset.UtcNow.ToString("O"))) request.ExecuteNonQuery();
            var remove = new List<string> { "external_watches", "watchlist_baselines", "watchlist_intents", "media_notifications", "notification_baselines", "deletion_authorizations" };
            if (version < 7) remove.Add("discord_controls");
            if (version < 6) remove.AddRange(["seerr_bindings", "request_operations"]);
            if (version < 5) remove.AddRange(["deletion_runs", "consent_delivery"]);
            foreach (var table in remove) { using var drop = HubStore.Command(db, "DROP TABLE " + table); drop.ExecuteNonQuery(); }
            using var old = HubStore.Command(db, "PRAGMA user_version=" + version); old.ExecuteNonQuery();
        }
        var upgraded = new HubStore(directory);
        using var ready = upgraded.Open();
        using (var schema = HubStore.Command(ready, "PRAGMA user_version")) Assert.Equal((long)HubStore.SchemaVersion, schema.ExecuteScalar());
        using (var personal = HubStore.Command(ready, "SELECT revision FROM personal")) Assert.Equal(7L, personal.ExecuteScalar());
        using (var queue = HubStore.Command(ready, "SELECT state FROM deletion_queue")) Assert.Equal("uncertain", queue.ExecuteScalar());
        if (version >= 5)
        {
            using var run = HubStore.Command(ready, "SELECT proof,state FROM deletion_runs"); using var r = run.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal(proof, r.GetString(0)); Assert.Equal("pinned-target", new SettingsService(upgraded).Unprotect(r.GetString(0))); Assert.Equal("files_deleted", r.GetString(1));
        }
        if (version >= 6) { using var request = HubStore.Command(ready, "SELECT state FROM request_operations"); Assert.Equal("sending", request.ExecuteScalar()); }
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
