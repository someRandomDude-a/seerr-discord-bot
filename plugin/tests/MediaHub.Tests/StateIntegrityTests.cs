using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class StateIntegrityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-state-integrity-" + Guid.NewGuid());
    [Theory]
    [InlineData("activity")] [InlineData("watches")] [InlineData("extras")] [InlineData("requests")]
    public void MissingSealNeverBecomesAnEmptyAuthorizedScope(string scope)
    {
        var store = new HubStore(directory); var settings = new SettingsService(store); settings.Protection.Check(scope);
        using (var db = store.Open()) using (var remove = HubStore.Command(db, "DELETE FROM state_seals WHERE scope=$0", scope)) remove.ExecuteNonQuery();
        Assert.Throws<HubError>(() => settings.Protection.Check(scope)); Assert.Throws<HubError>(() => new SettingsService(store).Protection.Check(scope));
        Assert.Throws<HubError>(() => settings.Protection.Update(scope, (_, _) => true));
    }
    [Fact]
    public void ReplayingAFormerlyValidScopeAndSealCannotRollBackThePrivateGeneration()
    {
        var store = new HubStore(directory); var settings = new SettingsService(store);
        settings.Protection.Update("watches", (db, tx) => { using var add = HubStore.Command(db, "INSERT INTO personal VALUES('viewer',0,'{}')"); add.Transaction = tx; return add.ExecuteNonQuery(); });
        string seal; using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT integrity FROM state_seals WHERE scope='watches'")) seal = (string)read.ExecuteScalar()!;
        settings.Protection.Update("watches", (db, tx) => { using var update = HubStore.Command(db, "UPDATE personal SET revision=1"); update.Transaction = tx; return update.ExecuteNonQuery(); });
        using (var db = store.Open()) using (var rollback = HubStore.Command(db, "UPDATE personal SET revision=0; UPDATE state_seals SET integrity=$0 WHERE scope='watches'", seal)) rollback.ExecuteNonQuery();
        Assert.Throws<HubError>(() => settings.Protection.Check("watches")); Assert.Throws<HubError>(() => new SettingsService(store).Protection.Check("watches"));
    }
    [Fact]
    public void MissingOutsideRootWithRetainedSealsIsNotRecreated()
    {
        var store = new HubStore(directory); _ = new SettingsService(store);
        File.Delete(Path.Combine(directory, "state-auth.json"));
        Assert.Throws<HubError>(() => new SettingsService(store)); Assert.False(File.Exists(Path.Combine(directory, "state-auth.json")));
    }
    [Fact]
    public void SchemaTwelveUpgradeRetainsUnsignedRowsButQuarantinesActivityAndWriteAuthorizers()
    {
        var store = new HubStore(directory); var settings = new SettingsService(store);
        using (var db = store.Open()) using (var old = HubStore.Command(db, "DROP TABLE state_seals; DROP TABLE watch_reviews; PRAGMA user_version=12; INSERT INTO activity VALUES('item','viewer',$0); INSERT INTO personal VALUES('viewer',3,'{}'); INSERT INTO extra_requests VALUES('extra','viewer','music','album','title','https://arr','proof',1,'uncertain',NULL,$0,$0)", DateTimeOffset.UtcNow.ToString("O"))) old.ExecuteNonQuery();
        File.Delete(Path.Combine(directory, "state-auth.json"));
        var upgraded = new HubStore(directory); var protectedSettings = new SettingsService(upgraded);
        Assert.Throws<HubError>(() => protectedSettings.Protection.RequireActivityWindow()); Assert.True(protectedSettings.Protection.NeedsWatchReview("viewer")); Assert.True(protectedSettings.Protection.LegacyRequest("extra"));
        protectedSettings.Protection.Check("activity"); protectedSettings.Protection.Check("watches"); protectedSettings.Protection.Check("extras");
        using var ready = upgraded.Open(); using var retained = HubStore.Command(ready, "SELECT state FROM extra_requests WHERE id='extra'"); Assert.Equal("uncertain", retained.ExecuteScalar());
    }
    [Fact]
    public void AnEmptyRetainedUnsignedDatabaseIsNotMistakenForFreshActivityHistoryWhenCreatingItsFirstKey()
    {
        var original = new HubStore(directory);
        using (var db = original.Open()) using (var old = HubStore.Command(db, "DROP TABLE state_seals; DROP TABLE watch_reviews; PRAGMA user_version=12")) old.ExecuteNonQuery();
        var retained = new HubStore(directory); Assert.False(retained.NewDatabaseCreated);
        var settings = new SettingsService(retained);
        Assert.Throws<HubError>(() => settings.Protection.RequireActivityWindow());
        settings.Protection.Check("activity"); Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
    }
    [Fact]
    public void MaintenanceIncludesNativePreferenceAndRequestOnlyUsersInBoundedOrder()
    {
        var store = new HubStore(directory); _ = new SettingsService(store);
        using (var db = store.Open()) using (var seed = HubStore.Command(db, "INSERT INTO personal VALUES('a',0,'{}'); INSERT INTO request_operations VALUES('r','b','https://seerr',1,'movie',1,'','completed','1',$0); INSERT INTO extra_requests VALUES('e','c','book','1','title','https://arr','proof',1,'completed','1',$0,$0)", DateTimeOffset.UtcNow.ToString("O"))) seed.ExecuteNonQuery();
        Assert.Equal(new[] { "a", "b", "c" }, MediaMaintenance.KnownUsers(store, "")); Assert.Equal(new[] { "b", "c" }, MediaMaintenance.KnownUsers(store, "a"));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
