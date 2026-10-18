using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Complete, ordered snapshot seals. Missing rows or seals fail closed, not just altered fields.
/// The private outside-SQLite root prevents a database writer from pretending a deleted scope is new.
/// Whole valid snapshot replay and OS/key compromise remain explicit trust boundaries.</summary>
internal sealed class StateIntegrity(HubStore store, SettingsService settings)
{
    private sealed record Root(string Id, DateTimeOffset? LegacySince, string[] LegacyUsers, string[] LegacyRequests, Dictionary<string, string> Versions, string Signature = "");
    private const string FileName = "state-auth.json";
    private static readonly Dictionary<string, string[]> Scopes = new()
    {
        ["activity"] = ["activity ORDER BY item,user"],
        ["watches"] = ["personal ORDER BY user", "subscriptions ORDER BY user,item", "external_watches ORDER BY user,kind,external_id", "watchlist_baselines ORDER BY user", "watchlist_intents ORDER BY user,kind,external_id", "extra_watches ORDER BY user,kind,foreign_id", "seerr_bindings ORDER BY user", "watch_reviews ORDER BY user"],
        ["extras"] = ["extra_requests ORDER BY id"],
        ["requests"] = ["request_operations ORDER BY id"]
    };
    private string RootSignature(Root root) => settings.Authenticate("MediaHub.StateRoot.v1", JsonSerializer.Serialize(root with { Signature = "" }));
    private Root ReadRoot()
    {
        var root = SafeJson.Read<Root>(Encoding.UTF8.GetString(StateFiles.ReadBounded(store.DirectoryPath, FileName, 1024 * 1024)), 1024 * 1024);
        if (root.Id.Length != 48 || !root.Id.All(Uri.IsHexDigit) || root.LegacyUsers.Length > 10000 || root.LegacyRequests.Length > 10000 || root.Versions.Count != Scopes.Count || Scopes.Keys.Any(scope => !root.Versions.TryGetValue(scope, out var version) || version.Length != 48 || !version.All(Uri.IsHexDigit)) ||
            !settings.Authenticated("MediaHub.StateRoot.v1", JsonSerializer.Serialize(root with { Signature = "" }), root.Signature)) throw new HubError("Protected state root cannot be verified. Restore a consistent stopped backup; nothing was reset.");
        return root;
    }
    internal void Initialize(bool createdKey)
    {
        if (System.IO.File.Exists(StateFiles.In(store.DirectoryPath, FileName))) { _ = ReadRoot(); return; }
        using var lease = StateFiles.Lease(store.DirectoryPath, "state-auth.lock");
        if (System.IO.File.Exists(StateFiles.In(store.DirectoryPath, FileName))) { _ = ReadRoot(); return; }
        if (System.IO.File.Exists(StateFiles.In(store.DirectoryPath, "state-auth.previous.json"))) throw new HubError("Protected state root is missing. Restore a consistent stopped backup; no root was recreated from its older checkpoint.");
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var exists = HubStore.Command(db, "SELECT COUNT(*) FROM state_seals"); exists.Transaction = tx;
        if ((long)exists.ExecuteScalar()! != 0) throw new HubError("Protected state root is missing. No seal was recreated.");
        var users = new List<string>(); var requests = new List<string>();
        using (var read = HubStore.Command(db, "SELECT user FROM personal UNION SELECT user FROM subscriptions UNION SELECT user FROM external_watches UNION SELECT user FROM watchlist_intents UNION SELECT user FROM watchlist_baselines UNION SELECT user FROM extra_watches LIMIT 10001"))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) users.Add(r.GetString(0)); }
        using (var read = HubStore.Command(db, "SELECT id FROM extra_requests UNION SELECT id FROM request_operations LIMIT 10001"))
        { read.Transaction = tx; using var r = read.ExecuteReader(); while (r.Read()) requests.Add(r.GetString(0)); }
        if (users.Count > 10000 || requests.Count > 10000) throw new HubError("Retained protection review exceeds its safe budget. Nothing was signed or reset.");
        using var activity = HubStore.Command(db, "SELECT COUNT(*) FROM activity"); activity.Transaction = tx;
        var legacy = !createdKey || !store.NewDatabaseCreated || users.Count != 0 || requests.Count != 0 || (long)activity.ExecuteScalar()! != 0;
        var root = new Root(HubStore.Nonce(), legacy ? DateTimeOffset.UtcNow : null, legacy ? users.ToArray() : [], legacy ? requests.ToArray() : [], Scopes.Keys.ToDictionary(scope => scope, _ => HubStore.Nonce()));
        root = root with { Signature = RootSignature(root) };
        var encoded = JsonSerializer.SerializeToUtf8Bytes(root);
        if (encoded.Length > 1024 * 1024) throw new HubError("Retained protection root exceeds its safe size. Nothing was signed or reset.");
        // If publication/commit is interrupted, mismatches block operations; never auto-reseal.
        foreach (var scope in Scopes.Keys) Seal(db, tx, scope, root);
        StateFiles.Publish(store.DirectoryPath, FileName, encoded); tx.Commit();
    }
    private static string Snapshot(SqliteConnection db, SqliteTransaction tx, string scope)
    {
        if (!Scopes.TryGetValue(scope, out var tables)) throw new InvalidOperationException("Unknown protected scope.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var rows = 0; long bytes = 0;
        foreach (var table in tables)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(table + "\n"));
            using var read = HubStore.Command(db, "SELECT * FROM " + table); read.Transaction = tx;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                if (++rows > 100000) throw new HubError("Protected state exceeds its row budget. Nothing was resealed.");
                var values = new object?[r.FieldCount]; for (var i = 0; i < values.Length; i++) values[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                var encoded = JsonSerializer.SerializeToUtf8Bytes(values); bytes += encoded.Length;
                if (bytes > 32 * 1024 * 1024) throw new HubError("Protected state exceeds its byte budget. Nothing was resealed.");
                hash.AppendData(encoded); hash.AppendData("\n"u8);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal void Check(SqliteConnection db, SqliteTransaction tx, string scope)
    {
        var root = ReadRoot(); using var read = HubStore.Command(db, "SELECT integrity FROM state_seals WHERE scope=$0", scope); read.Transaction = tx;
        if (read.ExecuteScalar() is not string seal || !settings.Authenticated("MediaHub.StateSet.v1|" + root.Id + "|" + scope + "|" + root.Versions[scope], Snapshot(db, tx, scope), seal))
            throw new HubError("Protected " + scope + " state changed or is incomplete. No write/retry/reset is authorized. Restore a consistent stopped backup.");
    }
    internal void Check(string scope)
    { using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true); Check(db, tx, scope); tx.Commit(); }
    internal string Fingerprint(SqliteConnection db, SqliteTransaction tx, string scope) { Check(db, tx, scope); return Snapshot(db, tx, scope); }
    private void Seal(SqliteConnection db, SqliteTransaction tx, string scope, Root root)
    {
        using var write = HubStore.Command(db, "INSERT INTO state_seals VALUES($0,$1) ON CONFLICT(scope) DO UPDATE SET integrity=excluded.integrity", scope, settings.Authenticate("MediaHub.StateSet.v1|" + root.Id + "|" + scope + "|" + root.Versions[scope], Snapshot(db, tx, scope))); write.Transaction = tx; write.ExecuteNonQuery();
    }
    internal void Seal(SqliteConnection db, SqliteTransaction tx, string scope)
    {
        // SQLite's immediate writer transaction serializes these checkpoint publications.
        // Advance a private outside-SQLite generation, so restoring an old *valid* table/seal
        // is also rejected. A crash between publication and commit blocks, never repairs.
        var root = ReadRoot();
        using (var existing = HubStore.Command(db, "SELECT integrity FROM state_seals WHERE scope=$0", scope)) {
            existing.Transaction = tx;
            if (existing.ExecuteScalar() is string seal && settings.Authenticated("MediaHub.StateSet.v1|" + root.Id + "|" + scope + "|" + root.Versions[scope], Snapshot(db, tx, scope), seal)) return;
        }
        var versions = new Dictionary<string, string>(root.Versions) { [scope] = HubStore.Nonce() };
        root = root with { Versions = versions, Signature = "" }; root = root with { Signature = RootSignature(root) };
        Seal(db, tx, scope, root); StateFiles.Publish(store.DirectoryPath, FileName, JsonSerializer.SerializeToUtf8Bytes(root), previous: "state-auth.previous.json");
    }
    internal T Update<T>(string scope, Func<SqliteConnection, SqliteTransaction, T> action)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction(); Check(db, tx, scope); var result = action(db, tx); Seal(db, tx, scope); tx.Commit(); return result;
    }
    internal bool NeedsWatchReview(string user)
    {
        var root = ReadRoot(); if (!root.LegacyUsers.Contains(user)) return false;
        using var db = store.Open(); using var tx = db.BeginTransaction(deferred: true); Check(db, tx, "watches");
        using var read = HubStore.Command(db, "SELECT 1 FROM watch_reviews WHERE user=$0", user); read.Transaction = tx; var needed = read.ExecuteScalar() is null; tx.Commit(); return needed;
    }
    internal bool LegacyRequest(string id) => ReadRoot().LegacyRequests.Contains(id);
    internal void RequireActivityWindow()
    {
        if (ReadRoot().LegacySince is { } since && DateTimeOffset.UtcNow < since.AddDays(settings.Current.RecentActivityDays))
            throw new HubError("Legacy local activity has no completeness proof. Removal is blocked until a full configured recent-activity window after the protected-state upgrade. Retained history was not reset.");
    }
}
