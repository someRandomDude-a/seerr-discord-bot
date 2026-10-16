using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaHub;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaHub.Tests;

public sealed class RecoveryTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-recovery-tests-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "hub.db");
    private string Settings => Path.Combine(directory, "settings.json");
    private string Key => Path.Combine(directory, "secrets.key");
    private static SettingsUpdate Update(int revision, string? token = "secret") => new(revision, false, "https://jellyfin.example/base", ["555"], "http://seerr", token, null);
    private static Action<string> FailAt(string point) => actual => { if (actual == point) throw new IOException("Simulated interrupted write"); };
    private static SqliteConnection Raw(string file, bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString()); db.Open(); return db;
    }
    private static long Scalar(SqliteConnection db, string sql) { using var command = HubStore.Command(db, sql); return (long)command.ExecuteScalar()!; }
    private void VersionOne()
    {
        Directory.CreateDirectory(directory); using var db = Raw(Database);
        using var schema = HubStore.Command(db, """
            CREATE TABLE links(discord TEXT PRIMARY KEY, user TEXT NOT NULL UNIQUE, approved INTEGER NOT NULL, token TEXT NOT NULL, device TEXT NOT NULL);
            CREATE TABLE approvals(hash TEXT PRIMARY KEY, discord TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE TABLE parties(id TEXT PRIMARY KEY, creator TEXT NOT NULL, session TEXT NOT NULL, group_id TEXT NOT NULL, name TEXT NOT NULL, expires INTEGER NOT NULL, state TEXT NOT NULL);
            CREATE UNIQUE INDEX active_creator ON parties(creator) WHERE state='active';
            CREATE TABLE invitations(id TEXT PRIMARY KEY, party TEXT NOT NULL, recipient TEXT, guild TEXT, channel TEXT, expires INTEGER NOT NULL);
            CREATE TABLE tickets(hash TEXT PRIMARY KEY, party TEXT NOT NULL, user TEXT NOT NULL, expires INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            INSERT INTO approvals VALUES('retained','42',9999999999);
            PRAGMA user_version=1;
            """); schema.ExecuteNonQuery();
        using var marker = HubStore.Command(db, "PRAGMA application_id=" + HubStore.ApplicationId); marker.ExecuteNonQuery();
    }
    [Theory]
    [InlineData("Database.BeforeCommit")]
    [InlineData("Database.BeforePublish")]
    public void InterruptedInitialCreationNeverPublishesAnEmptyOrPartialDatabase(string point)
    {
        Assert.Throws<IOException>(() => new HubStore(directory, FailAt(point)));
        Assert.False(File.Exists(Database));
        var reopened = new HubStore(directory); using var db = reopened.Open();
        Assert.Equal(HubStore.SchemaVersion, Scalar(db, "PRAGMA user_version"));
        using var check = HubStore.Command(db, "PRAGMA quick_check"); Assert.Equal("ok", check.ExecuteScalar());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Theory]
    [InlineData("Database.BackupReady")]
    [InlineData("Database.BeforeCommit")]
    public void InterruptedUpgradeRetainsOldSchemaDataAndVerifiedPreUpgradeSnapshot(string point)
    {
        VersionOne();
        Assert.Throws<IOException>(() => new HubStore(directory, FailAt(point)));
        using (var old = Raw(Database, true))
        {
            Assert.Equal(1, Scalar(old, "PRAGMA user_version"));
            Assert.Equal(1, Scalar(old, "SELECT COUNT(*) FROM approvals WHERE hash='retained'"));
            Assert.Equal(0, Scalar(old, "SELECT COUNT(*) FROM sqlite_master WHERE name='personal'"));
        }
        var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
        using (var backup = Raw(file, true))
        {
            Assert.Equal(1, Scalar(backup, "PRAGMA user_version"));
            Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM approvals WHERE hash='retained'"));
            using var check = HubStore.Command(backup, "PRAGMA quick_check"); Assert.Equal("ok", check.ExecuteScalar());
        }
        using var upgraded = new HubStore(directory).Open();
        Assert.Equal(HubStore.SchemaVersion, Scalar(upgraded, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(upgraded, "SELECT COUNT(*) FROM approvals WHERE hash='retained'"));
    }
    [Fact]
    public void UnavailableBackupStorageAbortsBeforeAnyUpgradeWrite()
    {
        VersionOne(); File.WriteAllText(Path.Combine(directory, "backups"), "must not overwrite");
        var before = SHA256.HashData(File.ReadAllBytes(Database));
        Assert.Throws<IOException>(() => new HubStore(directory));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Database)));
        Assert.Equal("must not overwrite", File.ReadAllText(Path.Combine(directory, "backups")));
    }
    [Fact]
    public void RepeatedFailedUpgradeDoesNotAccumulateIdenticalSnapshots()
    {
        VersionOne();
        for (var attempt = 0; attempt < 3; attempt++) Assert.Throws<IOException>(() => new HubStore(directory, FailAt("Database.BackupReady")));
        Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "backups"), "*.tmp"));
        using var original = Raw(Database, true); Assert.Equal(1, Scalar(original, "PRAGMA user_version"));
    }
    [Fact]
    public void InconsistentExistingSnapshotIsNeverOverwrittenAndUpgradeDoesNotProceed()
    {
        VersionOne(); Assert.Throws<IOException>(() => new HubStore(directory, FailAt("Database.BackupReady")));
        var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db"));
        File.WriteAllText(file, "damaged snapshot must be preserved for investigation");
        Assert.Throws<HubError>(() => new HubStore(directory));
        Assert.Equal("damaged snapshot must be preserved for investigation", File.ReadAllText(file));
        using var original = Raw(Database, true); Assert.Equal(1, Scalar(original, "PRAGMA user_version"));
    }
    [Fact]
    public void StartupIntegrityCheckRejectsInvalidSubscriptionStateWithoutResettingTheDatabase()
    {
        _ = new HubStore(directory);
        using (var corrupt = Raw(Database))
        using (var write = HubStore.Command(corrupt, "PRAGMA ignore_check_constraints=ON; INSERT INTO subscriptions VALUES('owner','item',9,0)")) write.ExecuteNonQuery();
        var before = SHA256.HashData(File.ReadAllBytes(Database));
        var error = Assert.Throws<HubError>(() => new HubStore(directory));
        Assert.Contains("integrity", error.Message); Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(Database)));
        using var retained = Raw(Database, true); Assert.Equal(9, Scalar(retained, "SELECT subscribed FROM subscriptions"));
    }
    [Fact]
    public void UpgradeSnapshotIncludesCommittedUncheckpointedWalData()
    {
        VersionOne(); using var writer = Raw(Database);
        using (var wal = HubStore.Command(writer, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO approvals VALUES('in-wal','7',9999999999)")) wal.ExecuteNonQuery();
        Assert.True(new FileInfo(Database + "-wal").Length > 0);
        _ = new HubStore(directory);
        using var backup = Raw(Assert.Single(Directory.GetFiles(Path.Combine(directory, "backups"), "*.db")), true);
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM approvals WHERE hash='in-wal'"));
    }
    [Fact]
    public void ConnectionDurabilityPoliciesAreExplicitAndUncommittedWritesRollback()
    {
        var store = new HubStore(directory); var code = store.BeginLink("42");
        using (var db = store.Open())
        {
            Assert.Equal(2, Scalar(db, "PRAGMA synchronous")); Assert.Equal(1, Scalar(db, "PRAGMA foreign_keys"));
            Assert.Equal(0, Scalar(db, "PRAGMA trusted_schema"));
            using var transaction = db.BeginTransaction(); using var erase = HubStore.Command(db, "DELETE FROM approvals");
            erase.Transaction = transaction; erase.ExecuteNonQuery();
        }
        var owner = Guid.NewGuid(); new HubStore(directory).Approve(code, owner, "42", "ciphertext", "device");
        Assert.Equal(owner, store.LinkedUser("42"));
    }
    [Fact]
    public void FailedReplacementLinkCodeDoesNotInvalidatePreviouslyIssuedApproval()
    {
        var store = new HubStore(directory); var code = store.BeginLink("42");
        var faulted = new HubStore(directory, FailAt("Link.BeforeInsert"));
        Assert.Throws<IOException>(() => faulted.BeginLink("42"));
        var owner = Guid.NewGuid(); store.Approve(code, owner, "42", "ciphertext", "device");
        Assert.Equal(owner, store.LinkedUser("42"));
    }
    [Fact]
    public void InterruptedKeyPublicationDoesNotPublishTruncatedKeyOrReplaceRetainedState()
    {
        var store = new HubStore(directory);
        Assert.Throws<IOException>(() => new SettingsService(store, FailAt("File.Flushed")));
        Assert.False(File.Exists(Key)); Assert.False(File.Exists(Settings));
        var settings = new SettingsService(store); settings.Update(Update(0));
        Assert.Equal(32, File.ReadAllBytes(Key).Length); Assert.Equal("secret", new SettingsService(store).DiscordToken());
    }
    [Fact]
    public void InterruptedSettingsPublicationPreservesCurrentAndPreviousVersions()
    {
        var store = new HubStore(directory); var settings = new SettingsService(store);
        settings.Update(Update(0, "old")); settings.Update(Update(1, "current"));
        var before = File.ReadAllBytes(Settings); var previous = File.ReadAllBytes(Path.Combine(directory, "settings.previous.json"));
        var faulted = new SettingsService(store, FailAt("File.Flushed"));
        Assert.Throws<IOException>(() => faulted.Update(Update(2, "never-published")));
        Assert.Equal(before, File.ReadAllBytes(Settings)); Assert.Equal(previous, File.ReadAllBytes(Path.Combine(directory, "settings.previous.json")));
        var reopened = new SettingsService(store); Assert.Equal(2, reopened.Current.Revision); Assert.Equal("current", reopened.DiscordToken());
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }
    [Fact]
    public void SuccessfulSettingsSavesKeepOneDecryptablePreviousVersion()
    {
        var settings = new SettingsService(new HubStore(directory));
        settings.Update(Update(0, "first")); settings.Update(Update(1, "second")); settings.Update(Update(2, "third"));
        var previous = JsonSerializer.Deserialize<HubSettings>(File.ReadAllText(Path.Combine(directory, "settings.previous.json")))!;
        Assert.Equal(2, previous.Revision); Assert.Equal("second", settings.Unprotect(previous.DiscordTokenEncrypted));
        Assert.Equal("third", settings.DiscordToken());
    }
    [Fact]
    public async Task SeparateSettingsInstancesCannotOverwriteEachOthersRevision()
    {
        var store = new HubStore(directory); var first = new SettingsService(store); var second = new SettingsService(store);
        var results = await Task.WhenAll(new[] { first, second }.Select(service => Task.Run(() =>
        {
            try { service.Update(Update(0)); return true; } catch (HubError) { return false; }
        })));
        Assert.Single(results, ok => ok); Assert.Equal(1, first.Current.Revision); Assert.Equal(1, second.Current.Revision);
        Assert.Throws<HubError>(() => second.Update(Update(0, "stale"))); Assert.Equal("secret", first.DiscordToken());
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("replaced")]
    [InlineData("truncated")]
    public void MissingOrChangedKeyWhileRunningBlocksEncryptionAndSettingsSaves(string problem)
    {
        var settings = new SettingsService(new HubStore(directory)); settings.Update(Update(0));
        var before = File.ReadAllBytes(Settings);
        if (problem == "missing") File.Delete(Key);
        else File.WriteAllBytes(Key, problem == "replaced" ? RandomNumberGenerator.GetBytes(32) : new byte[4]);
        Assert.Throws<HubError>(() => settings.Protect("new-credential"));
        Assert.Throws<HubError>(() => settings.DiscordToken()); Assert.Throws<HubError>(() => settings.Update(Update(1)));
        Assert.Equal(before, File.ReadAllBytes(Settings));
    }
    [Theory]
    [InlineData("future")]
    [InlineData("unknown-field")]
    [InlineData("null-array")]
    [InlineData("incomplete")]
    [InlineData("duplicate")]
    [InlineData("wrong-key")]
    public void InvalidOrNewerSettingsAreNotSilentlyRewrittenWithDefaults(string problem)
    {
        var store = new HubStore(directory); var settings = new SettingsService(store); settings.Update(Update(0));
        var node = JsonNode.Parse(File.ReadAllText(Settings))!;
        switch (problem)
        {
            case "future": node["FormatVersion"] = 999; break;
            case "unknown-field": node["FuturePreference"] = "retain me"; break;
            case "null-array": node["AllowedGuilds"] = null; break;
            case "incomplete": node = new JsonObject(); break;
            case "wrong-key": File.WriteAllBytes(Key, RandomNumberGenerator.GetBytes(32)); break;
        }
        var text = node.ToJsonString(); if (problem == "duplicate") text = text[..^1] + ",\"Revision\":0}";
        File.WriteAllText(Settings, text);
        Assert.ThrowsAny<Exception>(() => new SettingsService(store)); Assert.ThrowsAny<Exception>(() => settings.Update(Update(1)));
        Assert.Equal(text, File.ReadAllText(Settings));
    }
    [Fact]
    public void RetainedPreviousSettingsPreventNewKeyCreationOrSilentRecoveryWhenPrimaryIsMissing()
    {
        var store = new HubStore(directory); var settings = new SettingsService(store);
        settings.Update(Update(0)); settings.Update(Update(1));
        File.Delete(Settings);
        Assert.Throws<HubError>(() => new SettingsService(store));
        File.Delete(Key);
        Assert.Throws<HubError>(() => new SettingsService(store)); Assert.False(File.Exists(Key)); Assert.False(File.Exists(Settings));
        Assert.True(File.Exists(Path.Combine(directory, "settings.previous.json")));
    }
    [Theory]
    [InlineData("settings.json")]
    [InlineData("secrets.key")]
    public void OversizedStateIsRejectedWithoutUnboundedReadsOrReset(string name)
    {
        var store = new HubStore(directory); var settings = new SettingsService(store); settings.Update(Update(0));
        var path = Path.Combine(directory, name); File.WriteAllBytes(path, new byte[1024 * 1024]);
        Assert.Throws<HubError>(() => new SettingsService(store)); Assert.Throws<HubError>(() => settings.Update(Update(1)));
        Assert.Equal(1024 * 1024, new FileInfo(path).Length);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
