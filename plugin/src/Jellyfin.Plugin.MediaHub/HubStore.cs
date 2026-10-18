using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed class HubError(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>Plugin-owned database only; no host database connections, attachments or configuration writes.</summary>
public sealed class HubStore
{
    // V3 protects extended preferences; V4 adds activity, proposals, votes and notices.
    public const int SchemaVersion = 13;
    public const int ApplicationId = 0x4D485542; // MHUB, only inside hub.db.
    public string DirectoryPath { get; }
    internal bool NewDatabaseCreated { get; private set; }
    private readonly string connectionString;
    private readonly string databasePath;
    private readonly Action<string>? checkpoint;
    private int? verifiedSchema;
    internal const long DatabaseLimit = 512L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> Columns = new Dictionary<string, string>
    {
        ["links"] = "discord,user,approved,token,device", ["approvals"] = "hash,discord,expires",
        // Legacy watch-party tables are retained/validated, never redeemed or auto-migrated.
        ["parties"] = "id,creator,session,group_id,name,expires,state", ["invitations"] = "id,party,recipient,guild,channel,expires",
        ["tickets"] = "hash,party,user,expires,used", ["personal"] = "user,revision,preferences",
        ["subscriptions"] = "user,item,subscribed,created",
        ["activity"] = "item,user,at",
        ["deletion_queue"] = "id,owner,item,request_id,target,state,eligible,due,revision,created,delay",
        ["deletion_votes"] = "proposal,user,activity,approved",
        ["deletion_notices"] = "id,user,proposal,activity",
        ["deletion_runs"] = "proposal,proof,state,updated",
        ["consent_delivery"] = "notice,state,updated",
        ["seerr_bindings"] = "user,origin,seerr_id",
        ["request_operations"] = "id,user,origin,seerr_id,kind,external_id,payload,state,result,expires",
        ["discord_controls"] = "id,view_id,discord,user,message,payload,expires,state",
        ["external_watches"] = "user,kind,external_id,title,subscribed,native",
        ["watchlist_baselines"] = "user,payload",
        ["watchlist_intents"] = "user,kind,external_id,desired,revision,state",
        ["media_notifications"] = "id,user,item,status,created,seen,delivery",
        ["notification_baselines"] = "user,item,status",
        ["extra_requests"] = "id,user,kind,foreign_id,title,pin,parent,edition,state,result,expires,created",
        ["extra_watches"] = "user,kind,foreign_id,title,subscribed",
        ["message_plans"] = "id,actor,discord,mode,content,destinations,uploads,expires,state,request_id,fingerprint,created",
        ["message_deliveries"] = "id,plan,target,state,message_id,updated",
        ["message_files"] = "id,actor,filename,mime,body,expires,plan",
        ["message_inbox"] = "id,author,author_name,guild,guild_name,channel,channel_name,content,rich,created,edited,deleted,direction,peer,delivery",
        ["link_oauth"] = "state,handoff_hash,handoff,user,browser,verifier,pin,expires,status,discord,cookie",
        ["deletion_authorizations"] = "proposal,integrity",
        ["playback_operations"] = "id,user,state,payload",
        ["state_seals"] = "scope,integrity",
        ["watch_reviews"] = "user,payload"
    };
    public HubStore(IApplicationPaths paths) : this(Path.Combine(paths.DataPath, "media-hub")) { }
    public HubStore(string directory) : this(directory, null) { }
    internal HubStore(string directory, Action<string>? checkpoint)
    {
        this.checkpoint = checkpoint;
        DirectoryPath = Path.GetFullPath(directory);
        StateFiles.Check(DirectoryPath);
        if (!Directory.Exists(DirectoryPath))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
            else Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        databasePath = StateFiles.In(DirectoryPath, "hub.db");
        CheckPaths();
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString();
        if (File.Exists(databasePath))
        {
            if (VerifyDatabase(allowNativePreview: true)) return; // Already current: no schema/settings writes on load.
        }
        using var lease = StateFiles.Lease(DirectoryPath, "database.lock");
        CheckPaths();
        if (File.Exists(databasePath))
        {
            if (VerifyDatabase(allowNativePreview: true)) return;
            BackupBeforeUpgrade(); // If backup creation/verification fails, no migration writes occur.
            using var db = Connect(); Migrate(db);
        }
        else { CreateDatabase(); NewDatabaseCreated = true; }
        using var ready = Connect();
        using var wal = Command(ready, "PRAGMA journal_mode=WAL"); wal.ExecuteNonQuery();
        using var schema = Command(ready, "PRAGMA schema_version"); verifiedSchema = Convert.ToInt32(schema.ExecuteScalar());
    }
    private void Migrate(SqliteConnection db)
    {
        using var transaction = db.BeginTransaction();
        using var schema = Command(db, "PRAGMA user_version"); schema.Transaction = transaction;
        var version = Convert.ToInt32(schema.ExecuteScalar());
        if (version > SchemaVersion) throw new HubError("This state belongs to a newer Media Hub plugin. Restore the compatible plugin; no state was reset.");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS links(discord TEXT PRIMARY KEY, user TEXT NOT NULL UNIQUE, approved INTEGER NOT NULL,
                token TEXT NOT NULL, device TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS approvals(hash TEXT PRIMARY KEY, discord TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS parties(id TEXT PRIMARY KEY, creator TEXT NOT NULL, session TEXT NOT NULL,
                group_id TEXT NOT NULL, name TEXT NOT NULL, expires INTEGER NOT NULL, state TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS active_creator ON parties(creator) WHERE state='active';
            CREATE TABLE IF NOT EXISTS invitations(id TEXT PRIMARY KEY, party TEXT NOT NULL, recipient TEXT,
                guild TEXT, channel TEXT, expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS tickets(hash TEXT PRIMARY KEY, party TEXT NOT NULL, user TEXT NOT NULL,
                expires INTEGER NOT NULL, used INTEGER NOT NULL DEFAULT 0);
            """;
        // Recognize and upgrade the exact unversioned plugin-preview schema without resetting records.
        if (version < 1) command.ExecuteNonQuery();
        if (version < 2)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS personal(user TEXT PRIMARY KEY, revision INTEGER NOT NULL CHECK(revision>=0), preferences TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS subscriptions(user TEXT NOT NULL, item TEXT NOT NULL, subscribed INTEGER NOT NULL CHECK(subscribed IN (0,1)),
                    created INTEGER NOT NULL, PRIMARY KEY(user,item));
                """;
            command.ExecuteNonQuery();
        }
        if (version < 4)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS activity(item TEXT NOT NULL,user TEXT NOT NULL,at TEXT NOT NULL,PRIMARY KEY(item,user));
                CREATE TABLE IF NOT EXISTS deletion_queue(id TEXT PRIMARY KEY,owner TEXT NOT NULL,item TEXT NOT NULL,request_id INTEGER NOT NULL,
                    target TEXT NOT NULL,state TEXT NOT NULL,eligible TEXT,due TEXT,revision INTEGER NOT NULL,created TEXT NOT NULL,delay INTEGER NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS active_deletion_target ON deletion_queue(target) WHERE state NOT IN ('cancelled','completed');
                CREATE TABLE IF NOT EXISTS deletion_votes(proposal TEXT NOT NULL,user TEXT NOT NULL,activity TEXT NOT NULL,approved INTEGER NOT NULL CHECK(approved IN (0,1)),PRIMARY KEY(proposal,user));
                CREATE TABLE IF NOT EXISTS deletion_notices(id TEXT PRIMARY KEY,user TEXT NOT NULL,proposal TEXT NOT NULL,activity TEXT NOT NULL,UNIQUE(user,proposal,activity));
                """;
            command.ExecuteNonQuery();
        }
        if (version < 5)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS deletion_runs(proposal TEXT PRIMARY KEY,proof TEXT NOT NULL,state TEXT NOT NULL,updated TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS consent_delivery(notice TEXT PRIMARY KEY,state TEXT NOT NULL,updated TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }
        if (version < 6)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS seerr_bindings(user TEXT PRIMARY KEY,origin TEXT NOT NULL,seerr_id INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS request_operations(id TEXT PRIMARY KEY,user TEXT NOT NULL,origin TEXT NOT NULL,seerr_id INTEGER NOT NULL,
                    kind TEXT NOT NULL,external_id INTEGER NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL,result TEXT,expires TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS active_request_operation ON request_operations(user,origin,seerr_id,kind,external_id) WHERE state IN ('prepared','sending','uncertain');
                """;
            command.ExecuteNonQuery();
        }
        if (version < 7)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS discord_controls(id TEXT PRIMARY KEY,view_id TEXT NOT NULL,discord TEXT NOT NULL,user TEXT NOT NULL,
                    message TEXT,payload TEXT NOT NULL,expires TEXT NOT NULL,state TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS discord_control_view ON discord_controls(view_id);
                """;
            command.ExecuteNonQuery();
        }
        if (version < 8)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS external_watches(user TEXT NOT NULL,kind TEXT NOT NULL,external_id INTEGER NOT NULL,title TEXT NOT NULL,subscribed INTEGER NOT NULL,native TEXT,PRIMARY KEY(user,kind,external_id));
                CREATE TABLE IF NOT EXISTS watchlist_baselines(user TEXT PRIMARY KEY,payload TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS watchlist_intents(user TEXT NOT NULL,kind TEXT NOT NULL,external_id INTEGER NOT NULL,desired INTEGER NOT NULL,revision INTEGER NOT NULL,state TEXT NOT NULL,PRIMARY KEY(user,kind,external_id));
                CREATE TABLE IF NOT EXISTS media_notifications(id TEXT PRIMARY KEY,user TEXT NOT NULL,item TEXT NOT NULL,status TEXT NOT NULL,created TEXT NOT NULL,seen INTEGER NOT NULL,delivery TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS notification_baselines(user TEXT NOT NULL,item TEXT NOT NULL,status TEXT NOT NULL,PRIMARY KEY(user,item));
                """;
            command.ExecuteNonQuery();
        }
        if (version < 9)
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS extra_requests(id TEXT PRIMARY KEY,user TEXT NOT NULL,kind TEXT NOT NULL,foreign_id TEXT NOT NULL,title TEXT NOT NULL,pin TEXT NOT NULL,parent TEXT NOT NULL,edition TEXT,state TEXT NOT NULL,result INTEGER,expires TEXT NOT NULL,created TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS active_extra_request ON extra_requests(user,kind,foreign_id) WHERE state IN ('prepared','sending','uncertain');
                CREATE TABLE IF NOT EXISTS extra_watches(user TEXT NOT NULL,kind TEXT NOT NULL,foreign_id TEXT NOT NULL,title TEXT NOT NULL,subscribed INTEGER NOT NULL CHECK(subscribed IN (0,1)),PRIMARY KEY(user,kind,foreign_id));
                CREATE TABLE IF NOT EXISTS message_plans(id TEXT PRIMARY KEY,actor TEXT NOT NULL,discord TEXT NOT NULL,mode TEXT NOT NULL,content TEXT NOT NULL,destinations TEXT NOT NULL,uploads TEXT NOT NULL,expires TEXT NOT NULL,state TEXT NOT NULL,request_id TEXT,fingerprint TEXT NOT NULL,created TEXT NOT NULL);
                CREATE UNIQUE INDEX IF NOT EXISTS message_request_id ON message_plans(actor,request_id) WHERE request_id IS NOT NULL;
                CREATE TABLE IF NOT EXISTS message_deliveries(id TEXT PRIMARY KEY,plan TEXT NOT NULL,target TEXT NOT NULL,state TEXT NOT NULL,message_id TEXT,updated TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS message_files(id TEXT PRIMARY KEY,actor TEXT NOT NULL,filename TEXT NOT NULL,mime TEXT NOT NULL,body BLOB NOT NULL,expires TEXT NOT NULL,plan TEXT);
                CREATE TABLE IF NOT EXISTS message_inbox(id TEXT PRIMARY KEY,author TEXT NOT NULL,author_name TEXT NOT NULL,guild TEXT,guild_name TEXT,channel TEXT NOT NULL,channel_name TEXT NOT NULL,content TEXT NOT NULL,rich TEXT NOT NULL,created TEXT NOT NULL,edited TEXT,deleted INTEGER NOT NULL DEFAULT 0,direction TEXT NOT NULL,peer TEXT,delivery TEXT);
                """;
            command.ExecuteNonQuery();
        }
        if (version < 10)
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS link_oauth(state TEXT PRIMARY KEY,handoff_hash TEXT NOT NULL,handoff TEXT NOT NULL,user TEXT NOT NULL,browser TEXT NOT NULL,verifier TEXT NOT NULL,pin TEXT NOT NULL,expires TEXT NOT NULL,status TEXT NOT NULL,discord TEXT,cookie TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        if (version < 11)
        {
            // Never bless old consent/deadlines during migration. Retain old proposals for review/Undo only.
            command.CommandText = "CREATE TABLE IF NOT EXISTS deletion_authorizations(proposal TEXT PRIMARY KEY,integrity TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        if (version < 12)
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS playback_operations(id TEXT PRIMARY KEY,user TEXT NOT NULL,state TEXT NOT NULL,payload TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        if (version < 13)
        {
            // No old data is authorized by migration. Keyed initialization retains it for
            // explicit watchlist review and a full activity lookback quarantine.
            command.CommandText = "CREATE TABLE IF NOT EXISTS state_seals(scope TEXT PRIMARY KEY,integrity TEXT NOT NULL); CREATE TABLE IF NOT EXISTS watch_reviews(user TEXT PRIMARY KEY,payload TEXT NOT NULL)";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version=" + SchemaVersion; command.ExecuteNonQuery();
        command.CommandText = "PRAGMA application_id=" + ApplicationId; command.ExecuteNonQuery();
        Integrity(db, transaction);
        checkpoint?.Invoke("Database.BeforeCommit");
        transaction.Commit();
    }
    private void CreateDatabase()
    {
        var temp = StateFiles.In(DirectoryPath, "hub." + Nonce() + ".db.tmp"); var created = false;
        try
        {
            using (StateFiles.PrivateFile(temp)) created = true;
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp,
                Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString()))
            { db.Open(); Configure(db); Migrate(db); }
            checkpoint?.Invoke("Database.BeforePublish");
            CheckPaths(); File.Move(temp, databasePath, false); // Only a complete, committed schema becomes hub.db.
        }
        finally { if (created) StateFiles.DeleteTemporary(temp); }
    }
    private void BackupBeforeUpgrade()
    {
        var directory = Path.Combine(DirectoryPath, "backups"); StateFiles.Check(directory);
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var temp = StateFiles.In(directory, "hub-before-upgrade-" + Nonce() + ".db.tmp"); var created = false;
        try
        {
            using (StateFiles.PrivateFile(temp)) created = true;
            using (var source = ReadOnly())
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp,
                Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 2 }.ToString()))
            { backup.Open(); Configure(backup); source.BackupDatabase(backup); Integrity(backup); }
            using (var file = new FileStream(temp, FileMode.Open, FileAccess.Write)) file.Flush(true);
            string digest;
            using (var file = File.OpenRead(temp)) digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            var final = StateFiles.In(directory, "hub-before-upgrade-" + digest + ".db");
            if (File.Exists(final))
            {
                using var retained = File.OpenRead(final);
                if (Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant() != digest)
                    throw new HubError("An existing Media Hub upgrade snapshot is inconsistent. It was not overwritten.");
            }
            else File.Move(temp, final, false); // Identical failed-upgrade retries do not accumulate snapshots.
            checkpoint?.Invoke("Database.BackupReady");
        }
        finally { if (created) StateFiles.DeleteTemporary(temp); }
    }
    private static void ReadLimits(SqliteConnection db)
    {
        // Connection-local limits only. Never change SQLite's global heap, host pools or host connections.
        SQLitePCL.raw.sqlite3_limit(db.Handle, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 9 * 1024 * 1024);
        SQLitePCL.raw.sqlite3_limit(db.Handle, SQLitePCL.raw.SQLITE_LIMIT_EXPR_DEPTH, 64);
        using var trust = Command(db, "PRAGMA trusted_schema=OFF"); trust.ExecuteNonQuery();
    }
    private static void Configure(SqliteConnection db)
    {
        ReadLimits(db);
        using var policy = Command(db, "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;"); policy.ExecuteNonQuery();
        using var page = Command(db, "PRAGMA page_size"); var size = Convert.ToInt32(page.ExecuteScalar());
        if (size < 512 || size > 65536) throw new HubError("Unsupported plugin database page size.");
        using var limit = Command(db, "PRAGMA max_page_count=" + (DatabaseLimit / size));
        if (Convert.ToInt64(limit.ExecuteScalar()) * size > DatabaseLimit) throw new HubError("Plugin database exceeds its storage budget. No reset was performed.");
    }
    private static void Integrity(SqliteConnection db, SqliteTransaction? transaction = null)
    {
        using var command = Command(db, "PRAGMA quick_check"); command.Transaction = transaction; var count = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            { if (reader.GetString(0) != "ok") throw new HubError("Media Hub database integrity verification failed. Restore a consistent backup; no reset was performed."); count++; }
        if (count != 1) throw new HubError("Media Hub database integrity could not be confirmed. No reset was performed.");
        // Physical checks do not establish application invariants on every SQLite build
        // or on older preview tables without CHECK constraints. Validate these explicitly.
        foreach (var (table, predicate) in new[] { ("personal", "typeof(revision)<>'integer' OR revision<0"),
            ("subscriptions", "typeof(subscribed)<>'integer' OR subscribed NOT IN (0,1)") })
        {
            using var exists = Command(db, "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$0", table); exists.Transaction = transaction;
            if (exists.ExecuteScalar() is null) continue; // Version-one snapshots do not have personal tables.
            using var invalid = Command(db, "SELECT 1 FROM " + table + " WHERE " + predicate + " LIMIT 1"); invalid.Transaction = transaction;
            if (invalid.ExecuteScalar() is not null) throw new HubError("Media Hub database integrity/domain verification failed. No state was reset.");
        }
    }
    private void CheckPaths()
    {
        foreach (var file in new[] { "hub.db", "hub.db-wal", "hub.db-shm", "hub.db-journal" })
        {
            var path = StateFiles.In(DirectoryPath, file);
            if (File.Exists(path) && new FileInfo(path).Length > (file == "hub.db" ? DatabaseLimit : 128L * 1024 * 1024))
                throw new HubError("Plugin database or journal exceeds its storage budget. No file was reset or truncated.");
        }
    }
    private bool VerifyDatabase(bool allowNativePreview = false)
    {
        CheckPaths();
        using var db = ReadOnly();
        using var marker = Command(db, "PRAGMA application_id"); var application = Convert.ToInt32(marker.ExecuteScalar());
        using var schema = Command(db, "PRAGMA user_version"); var version = Convert.ToInt32(schema.ExecuteScalar());
        if (application != ApplicationId && !(allowNativePreview && application == 0))
            throw new HubError("This file is not a Media Hub database. It was not changed.");
        if (version > SchemaVersion) throw new HubError("This state belongs to a newer Media Hub plugin. No state was reset.");
        var current = application == ApplicationId && version == SchemaVersion;
        if (!allowNativePreview)
        {
            if (!current) throw new HubError("Media Hub database schema changed. Restart the compatible plugin before acting; no write was attempted.");
            using var liveSchema = Command(db, "PRAGMA schema_version");
            if (verifiedSchema is null || Convert.ToInt32(liveSchema.ExecuteScalar()) != verifiedSchema) throw new HubError("Plugin database structure changed during use. No write was attempted.");
            return true;
        }
        // Adopt only the exact known native preview, never an arbitrary unmarked database.
        using var tables = Command(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        var names = new List<string>(); using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var required = version >= 13 ? Columns.Keys : version >= 12 ? Columns.Keys.Take(30) : version >= 11 ? Columns.Keys.Take(29) : version >= 10 ? Columns.Keys.Take(28) : version >= 9 ? Columns.Keys.Take(27) : version >= 8 ? Columns.Keys.Take(21) : version >= 7 ? Columns.Keys.Take(16) : version >= 6 ? Columns.Keys.Take(15) : version >= 5 ? Columns.Keys.Take(13) : version >= 4 ? Columns.Keys.Take(11) : version >= 2 ? Columns.Keys.Take(7) : Columns.Keys.Take(5);
        if (required.Except(names, StringComparer.Ordinal).Any() || names.Except(Columns.Keys, StringComparer.Ordinal).Any())
            throw new HubError("Unrecognized Media Hub database schema. It was not changed.");
        foreach (var name in names)
        {
            using var columns = Command(db, "SELECT name FROM pragma_table_info($0) ORDER BY cid", name);
            var found = new List<string>(); using var reader = columns.ExecuteReader(); while (reader.Read()) found.Add(reader.GetString(0));
            if (string.Join(',', found) != Columns[name]) throw new HubError("Unrecognized Media Hub table. It was not changed.");
        }
        using var extensions = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('view','trigger')");
        if ((long)extensions.ExecuteScalar()! != 0) throw new HubError("Unrecognized Media Hub schema extensions. No migration was attempted.");
        Integrity(db);
        if (current) { using var liveSchema = Command(db, "PRAGMA schema_version"); verifiedSchema = Convert.ToInt32(liveSchema.ExecuteScalar()); }
        return current;
    }
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public static string Nonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private SqliteConnection Connect()
    {
        var db = new SqliteConnection(connectionString);
        try { db.Open(); Configure(db); return db; } catch { db.Dispose(); throw; }
    }
    private SqliteConnection ReadOnly()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
        try { db.Open(); ReadLimits(db); return db; } catch { db.Dispose(); throw; }
    }
    public SqliteConnection Open() { VerifyDatabase(); return Connect(); }
    public static SqliteCommand Command(SqliteConnection db, string sql, params object?[] args)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return command;
    }
    public Guid LinkedUser(string discord)
    {
        using var db = Open(); using var command = Command(db, "SELECT user FROM links WHERE discord=$0", discord);
        return command.ExecuteScalar() is string value ? Guid.Parse(value) : throw new HubError("Link your account first with /hub-link, then approve it in Jellyfin.");
    }
    public (Guid User, string Token, string Device) Credential(string discord)
    {
        using var db = Open(); using var command = Command(db, "SELECT user,token,device FROM links WHERE discord=$0", discord);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new HubError("Link your account first with /hub-link, then approve it in Jellyfin.");
        return (Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2));
    }
    public bool IsLinked(Guid user)
    {
        using var db = Open(); using var command = Command(db, "SELECT 1 FROM links WHERE user=$0", user.ToString("N"));
        return command.ExecuteScalar() is not null;
    }
    public string DiscordFor(Guid user)
    {
        using var db = Open(); using var command = Command(db, "SELECT discord FROM links WHERE user=$0", user.ToString("N"));
        return command.ExecuteScalar() as string ?? throw new HubError("Link Discord first with /hub-link.");
    }
    public bool HasCredentials()
    {
        using var db = Open(); using var command = Command(db, "SELECT 1 FROM links UNION ALL SELECT 1 FROM deletion_runs UNION ALL SELECT 1 FROM extra_requests UNION ALL SELECT 1 FROM link_oauth UNION ALL SELECT 1 FROM request_operations UNION ALL SELECT 1 FROM discord_controls UNION ALL SELECT 1 FROM message_plans UNION ALL SELECT 1 FROM deletion_authorizations UNION ALL SELECT 1 FROM playback_operations UNION ALL SELECT 1 FROM state_seals LIMIT 1"); return command.ExecuteScalar() is not null;
    }
    public string BeginLink(string discord)
    {
        var code = Nonce(); using var db = Open(); using var transaction = db.BeginTransaction();
        using (var prune = Command(db, "DELETE FROM approvals WHERE expires<=$0 OR discord=$1", Now, discord))
        { prune.Transaction = transaction; prune.ExecuteNonQuery(); }
        checkpoint?.Invoke("Link.BeforeInsert");
        using var command = Command(db, "INSERT INTO approvals VALUES($0,$1,$2)", Hash(code), discord, Now + 300);
        command.Transaction = transaction; command.ExecuteNonQuery(); transaction.Commit(); return code;
    }
    public string PendingDiscord(string code)
    {
        using var db = Open(); using var read = Command(db, "SELECT discord FROM approvals WHERE hash=$0 AND expires>$1", Hash(code), Now);
        return read.ExecuteScalar() as string ?? throw new HubError("Link expired or was already used. Request a new link.");
    }
    public void Approve(string code, Guid user, string expectedDiscord, string encryptedToken, string device, string? oauthState = null, string? browser = null, string? completedHandoff = null)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        if (oauthState is not null)
        {
            if (completedHandoff is null) throw new HubError("Authenticated completion proof is required.");
            using var proof = Command(db, "UPDATE link_oauth SET status='used',handoff=$6 WHERE state=$0 AND handoff_hash=$1 AND user=$2 AND browser=$3 AND discord=$4 AND status='proved' AND expires>$5", oauthState, Hash(code), user.ToString("N"), browser, expectedDiscord, DateTimeOffset.UtcNow.ToString("O"), completedHandoff); proof.Transaction = transaction;
            if (proof.ExecuteNonQuery() != 1) throw new HubError("Both browser identities must be verified again before linking.");
        }
        using var pending = Command(db, "SELECT discord FROM approvals WHERE hash=$0 AND expires>$1", Hash(code), Now);
        pending.Transaction = transaction;
        if (pending.ExecuteScalar() is not string discord || discord != expectedDiscord) throw new HubError("Invalid, expired, or mismatched approval code.");
        using var existing = Command(db, "SELECT discord,user FROM links WHERE discord=$0 OR user=$1", discord, user.ToString("N"));
        existing.Transaction = transaction;
        using (var reader = existing.ExecuteReader())
            while (reader.Read())
                if (reader.GetString(0) != discord || reader.GetString(1) != user.ToString("N"))
                    throw new HubError("This Discord/Jellyfin identity is already bound to another account.");
        using var link = Command(db, "INSERT INTO links VALUES($0,$1,$2,$3,$4) ON CONFLICT(discord) " +
            "DO UPDATE SET approved=excluded.approved,token=excluded.token,device=excluded.device", discord, user.ToString("N"), Now, encryptedToken, device);
        link.Transaction = transaction; link.ExecuteNonQuery();
        using var consume = Command(db, "DELETE FROM approvals WHERE hash=$0", Hash(code));
        consume.Transaction = transaction; consume.ExecuteNonQuery(); transaction.Commit();
    }
}
