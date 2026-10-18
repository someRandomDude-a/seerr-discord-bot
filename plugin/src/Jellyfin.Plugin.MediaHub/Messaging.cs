using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaHub;

public sealed record MessageTarget(string Kind, string Id, string? Guild = null);
public sealed record MessageDestination(MessageTarget Target, string Label);
public sealed record MessageUpload(string Id, string Filename, long Size, string Mime);
public sealed record SendPreview(string Id, string Mode, string Content, IReadOnlyList<MessageDestination> Destinations, IReadOnlyList<MessageUpload> Files, DateTimeOffset Expires, string State);
public sealed record MessageDelivery(string Id, string State, string? MessageId, string Destination);
public sealed record SendReceipt(string Id, string Mode, string Content, IReadOnlyList<MessageDelivery> Deliveries, DateTimeOffset Created);
public sealed record MessageView(string Id, string Author, string AuthorName, string? Guild, string? GuildName, string Channel, string ChannelName,
    string Content, JsonNode Rich, DateTimeOffset Created, DateTimeOffset? Edited, bool Deleted, string Direction, string? Peer, string? DeliveryState);
public sealed record InboxPage(IReadOnlyList<MessageView> Messages, int Page, bool HasNext);
public sealed record MessageBlob(byte[] Bytes, string Filename, string Mime);

public interface IDiscordMessaging
{
    Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor, CancellationToken cancellation);
    Task<IReadOnlyList<MessageTarget>> Members(OperatorActor actor, string guild, CancellationToken cancellation);
    Task<MessageDestination> Verify(OperatorActor actor, MessageTarget target, bool writing, bool files, CancellationToken cancellation);
    Task<string> Send(MessageTarget target, string content, IReadOnlyList<MessageBlob> files, CancellationToken cancellation);
}

/// <summary>Private operator inbox and transactional outbound intents. Never sends directly from a browser-provided target label.</summary>
public sealed class Messaging(HubStore store, SettingsService settings, OperatorAuthority operators, IDiscordMessaging discord)
{
    private sealed record FileSeal(string Id, string Filename, string Mime, long Size, string Digest);
    private sealed record PlanSeal(string Request, int ConfigurationRevision, FileSeal[] Files, string Integrity);
    private sealed record DeliverySeal(MessageDestination Destination, string Integrity);
    private string PlanData(OperatorActor actor, SendPreview plan, string request, FileSeal[] files, int revision) => JsonSerializer.Serialize(new { ConfigurationRevision = revision, Actor = Actor(actor), actor.Discord, plan.Id, plan.Mode, plan.Content, plan.Destinations, Uploads = files.Select(f => f.Id), plan.Expires, plan.State, Request = request, Files = files });
    private string SealPlan(OperatorActor actor, SendPreview plan, string request, FileSeal[] files, int revision) => JsonSerializer.Serialize(new PlanSeal(request, revision, files, settings.Authenticate("MediaHub.MessagePlan.v1", PlanData(actor, plan, request, files, revision))));
    private PlanSeal CheckPlan(OperatorActor actor, SendPreview plan, string encoded)
    {
        var seal = SafeJson.Read<PlanSeal>(encoded, 8192);
        if (seal.Files is null || seal.Files.Length > 4 || seal.Files.Any(f => f is null) ||
            !plan.Files.Select(f => f.Id).ToHashSet().SetEquals(seal.Files.Select(f => f.Id)) ||
            !settings.Authenticated("MediaHub.MessagePlan.v1", PlanData(actor, plan, seal.Request, seal.Files, seal.ConfigurationRevision), seal.Integrity)) throw new HubError("Message plan integrity verification failed. No send was authorized.");
        return seal;
    }
    private string DeliveryData(string id, string plan, OperatorActor actor, string state, MessageDestination destination) => JsonSerializer.Serialize(new { id, plan, Actor = Actor(actor), actor.Discord, state, destination });
    private string SealDelivery(string id, string plan, OperatorActor actor, string state, MessageDestination destination) => JsonSerializer.Serialize(new DeliverySeal(destination, settings.Authenticate("MediaHub.MessageDelivery.v1", DeliveryData(id, plan, actor, state, destination))));
    private MessageDestination CheckDelivery(string encoded, string id, string plan, OperatorActor actor, string state)
    {
        var value = SafeJson.Read<DeliverySeal>(encoded, 8192);
        if (value.Destination is null || !settings.Authenticated("MediaHub.MessageDelivery.v1", DeliveryData(id, plan, actor, state, value.Destination), value.Integrity)) throw new HubError("Delivery integrity verification failed. No send was authorized.");
        Target(value.Destination.Target); return value.Destination;
    }
    public const int FileLimit = 8 * 1024 * 1024;
    private static string Time(DateTimeOffset value) => value.ToString("O");
    private static string Actor(OperatorActor actor) => actor.User.ToString("N");
    private static void Snowflake(string id) { if (id.Length == 0 || id[0] == '0' || id.Any(c => c is < '0' or > '9') || !ulong.TryParse(id, out var n) || n == 0) throw new HubError("Invalid Discord ID."); }
    private static void Target(MessageTarget target)
    { if (target.Kind is not ("channel" or "user")) throw new HubError("Invalid destination."); Snowflake(target.Id); if (target.Guild is not null) Snowflake(target.Guild); if (target.Kind == "channel" && target.Guild is null) throw new HubError("Specify the destination server."); }
    public async Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor, CancellationToken cancellation)
    { await operators.Check(actor, cancellation); var result = await discord.Destinations(actor, cancellation); await operators.Check(actor, cancellation); return result; }
    private void Prune(SqliteConnection db, SqliteTransaction tx)
    {
        using var files = HubStore.Command(db, "DELETE FROM message_files WHERE expires<=$0", Time(DateTimeOffset.UtcNow)); files.Transaction = tx; files.ExecuteNonQuery();
        using var inbox = HubStore.Command(db, "DELETE FROM message_inbox WHERE created<$0 OR id NOT IN (SELECT id FROM message_inbox ORDER BY created DESC,id LIMIT 10000)", Time(DateTimeOffset.UtcNow.AddDays(-settings.Current.Features.MessageRetentionDays))); inbox.Transaction = tx; inbox.ExecuteNonQuery();
        var cutoff = Time(DateTimeOffset.UtcNow.AddDays(-settings.Current.Features.MessageRetentionDays));
        // Keep small immutable idempotency tombstones, not private message content or destinations indefinitely.
        using var deliveries = HubStore.Command(db, "UPDATE message_deliveries SET target=$0,message_id=NULL,state=CASE WHEN state='pending' THEN 'expired' WHEN state='sending' THEN 'uncertain' ELSE state END WHERE plan IN (SELECT id FROM message_plans WHERE created<$1)", JsonSerializer.Serialize(new MessageDestination(new("expired", "0"), "Expired")), cutoff); deliveries.Transaction = tx; deliveries.ExecuteNonQuery();
        using var plans = HubStore.Command(db, "UPDATE message_plans SET content='',destinations='[]',uploads='[]',state='retained',fingerprint=CASE WHEN json_valid(fingerprint) THEN json_object('Request',json_extract(fingerprint,'$.Request'),'ConfigurationRevision',0,'Files',json('[]'),'Integrity','') ELSE fingerprint END WHERE created<$0", cutoff); plans.Transaction = tx; plans.ExecuteNonQuery();
    }
    internal static string Filename(string name)
    {
        name = name.Replace('\\', '/').Split('/').Last(); name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or ':' or '<' or '>' or '|')).Take(120).ToArray()).Trim();
        return string.IsNullOrEmpty(name) || name is "." or ".." ? "file" : name;
    }
    internal static string Mime(byte[] bytes)
    {
        // Only raster magic is trusted. HTML/SVG and MIME labels supplied by Discord/browser remain downloads.
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var w = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)); var h = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (w is > 0 and <= 8192 && h is > 0 and <= 8192 && (long)w * h <= 16000000) return "image/png";
        }
        return "application/octet-stream";
    }
    private IEnumerable<string> Secrets()
    {
        var s = settings.Current;
        return new[] { settings.Unprotect(s.DiscordTokenEncrypted), settings.Unprotect(s.SeerrKeyEncrypted), settings.Unprotect(s.Features.Lidarr.KeyEncrypted), settings.Unprotect(s.Features.Readarr.KeyEncrypted), settings.Unprotect(s.Features.DiscordClientSecretEncrypted) }.Where(v => v.Length >= 8);
    }
    private void CheckSecrets(string content, IEnumerable<MessageBlob> files)
    {
        foreach (var secret in Secrets())
        {
            var bytes = Encoding.UTF8.GetBytes(secret);
            if (content.Contains(secret, StringComparison.Ordinal) || files.Any(f => f.Bytes.AsSpan().IndexOf(bytes) >= 0)) throw new HubError("This message or attachment contains a configured secret. Remove it before sending.");
        }
    }
    public async Task<MessageUpload> Upload(OperatorActor actor, string filename, byte[] body, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation);
        if (body.Length is 0 or > FileLimit) throw new HubError("Each upload must contain 1 byte to 8 MiB.");
        filename = Filename(filename); var mime = Mime(body); var id = HubStore.Nonce();
        using var db = store.Open(); using var tx = db.BeginTransaction(); Prune(db, tx);
        using var count = HubStore.Command(db, "SELECT COALESCE(SUM(LENGTH(body)),0) FROM message_files"); count.Transaction = tx;
        if ((long)count.ExecuteScalar()! + body.Length > 64 * 1024 * 1024) throw new HubError("Attachment storage is full (64 MiB). Wait for retention expiry.");
        using var add = HubStore.Command(db, "INSERT INTO message_files VALUES($0,$1,$2,$3,$4,$5,NULL)", id, Actor(actor), filename, mime, body, Time(DateTimeOffset.UtcNow.AddMinutes(15))); add.Transaction = tx; add.ExecuteNonQuery(); tx.Commit();
        return new(id, filename, body.Length, mime);
    }
    private List<(MessageUpload Meta, MessageBlob Blob)> Files(SqliteConnection db, OperatorActor actor, string[] ids, string? plan = null, SqliteTransaction? tx = null)
    {
        if (ids.Length > 4 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new HubError("Choose up to four different uploads.");
        var result = new List<(MessageUpload Meta, MessageBlob Blob)>(); var total = 0L;
        foreach (var id in ids)
        {
            using var read = HubStore.Command(db, "SELECT filename,mime,CASE WHEN LENGTH(body) BETWEEN 1 AND 8388608 THEN body ELSE NULL END,plan FROM message_files WHERE id=$0 AND actor=$1 AND expires>$2", id, Actor(actor), Time(DateTimeOffset.UtcNow)); read.Transaction = tx; using var r = read.ExecuteReader();
            if (!r.Read() || r.IsDBNull(2) || !r.IsDBNull(3) && r.GetString(3) != plan) throw new HubError("Upload expired, exceeds its size limit, was already claimed, or belongs to another operator.");
            var body = (byte[])r[2]; total += body.Length; if (body.Length is 0 or > FileLimit || total > 16 * 1024 * 1024) throw new HubError("Attachments exceed their safe size limit.");
            var filename = Filename(r.GetString(0)); var mime = Mime(body);
            result.Add((new(id, filename, body.Length, mime), new(body, filename, mime)));
        }
        return result;
    }
    public async Task<MessageBlob> DownloadUpload(OperatorActor actor, string id, CancellationToken cancellation)
    { await operators.Check(actor, cancellation); using var db = store.Open(); return Files(db, actor, [id], PlanForFile(db, id, actor)).Single().Blob; }
    private static string? PlanForFile(SqliteConnection db, string id, OperatorActor actor)
    { using var read = HubStore.Command(db, "SELECT plan FROM message_files WHERE id=$0 AND actor=$1", id, Actor(actor)); return read.ExecuteScalar() as string; }
    public async Task<SendPreview> Prepare(OperatorActor actor, string mode, string content, MessageTarget[] targets, string[] uploads, string? requestId, bool members, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation);
        if (mode is not ("normal" or "announcement") || content.Length > 2000 || content.Any(c => c == '\0') || content.Trim().Length == 0 && uploads.Length == 0 || targets.Length is < 1 or > 1000 || mode == "normal" && (targets.Length != 1 || members)) throw new HubError("Normal chat needs one explicit destination. Broadcasts require an announcement preview.");
        if (requestId is not null && !System.Text.RegularExpressions.Regex.IsMatch(requestId, "\\A[A-Za-z0-9_-]{16,80}\\z")) throw new HubError("Use a unique send ID.");
        if (mode == "normal" && requestId is null) throw new HubError("A unique send ID is required for normal chat.");
        var resolved = new Dictionary<(string Kind, string Id), MessageDestination>(); var selected = targets.OrderBy(t => t.Kind).ThenBy(t => t.Guild).ThenBy(t => t.Id).Distinct().ToArray();
        var fingerprint = settings.Authenticate("MediaHub.MessageNonce.v1", JsonSerializer.Serialize(new { Actor = Actor(actor), actor.Discord, mode, content, selected, uploads, members, requestId }));
        using (var db = store.Open())
        {
            if (requestId is not null)
            {
                using var old = HubStore.Command(db, "SELECT id,fingerprint FROM message_plans WHERE actor=$0 AND request_id=$1", Actor(actor), requestId); using var r = old.ExecuteReader();
                if (r.Read()) { if (SafeJson.Read<PlanSeal>(r.GetString(1), 8192).Request != fingerprint) throw new HubError("That send ID belongs to a different message."); var prior = r.GetString(0); r.Close(); return Preview(db, actor, prior); }
            }
        }
        List<(MessageUpload Meta, MessageBlob Blob)> fileRows;
        using (var db = store.Open()) fileRows = Files(db, actor, uploads);
        CheckSecrets(content, fileRows.Select(f => f.Blob)); var f = settings.Current.Features;
        foreach (var target in selected)
        {
            Target(target);
            resolved[(target.Kind, target.Id)] = await discord.Verify(actor, target, true, uploads.Length > 0, cancellation);
            if (members)
            {
                if (!f.MembersIntent || target.Guild is null || target.Kind != "channel") throw new HubError("All-member announcements need explicit servers and the enabled Members Intent.");
                foreach (var member in await discord.Members(actor, target.Guild, cancellation))
                {
                    Target(member); if (resolved.ContainsKey((member.Kind, member.Id))) continue; resolved[(member.Kind, member.Id)] = await discord.Verify(actor, member, true, uploads.Length > 0, cancellation);
                    if (resolved.Count > f.MaxRecipients) throw new HubError("Recipient limit exceeded. No messages were sent.");
                }
            }
        }
        if (resolved.Count > f.MaxRecipients) throw new HubError("Recipient limit exceeded. No messages were sent.");
        await operators.Check(actor, cancellation);
        var id = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        var fileSeals = fileRows.Select(f => new FileSeal(f.Meta.Id, f.Blob.Filename, f.Blob.Mime, f.Blob.Bytes.Length, Convert.ToHexString(SHA256.HashData(f.Blob.Bytes)))).ToArray();
        var preview = new SendPreview(id, mode, content, resolved.Values.ToArray(), fileRows.Select(f => f.Meta).ToArray(), expires, "prepared");
        var authorization = SealPlan(actor, preview, fingerprint, fileSeals, settings.Current.Revision);
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            Prune(db, tx);
            using var capacity = HubStore.Command(db, "SELECT COUNT(*) FROM message_plans"); capacity.Transaction = tx;
            if ((long)capacity.ExecuteScalar()! >= 50000) throw new HubError("Message journal capacity reached. Retained uncertain outcomes were not discarded.");
            using var rate = HubStore.Command(db, "SELECT COUNT(*) FROM message_plans WHERE actor=$0 AND mode=$1 AND created>$2", Actor(actor), mode, Time(DateTimeOffset.UtcNow.AddMinutes(mode == "normal" ? -1 : -5))); rate.Transaction = tx;
            if ((long)rate.ExecuteScalar()! >= (mode == "normal" ? 30 : 5)) throw new HubError("Send limit reached. Wait before preparing another message.");
            _ = Files(db, actor, uploads, tx: tx);
            using var add = HubStore.Command(db, "INSERT INTO message_plans VALUES($0,$1,$2,$3,$4,$5,$6,$7,'prepared',$8,$9,$10)", id, Actor(actor), actor.Discord, mode, content, JsonSerializer.Serialize(resolved.Values), JsonSerializer.Serialize(uploads), Time(expires), requestId, authorization, Time(DateTimeOffset.UtcNow)); add.Transaction = tx; add.ExecuteNonQuery(); tx.Commit();
        }
        using (var db = store.Open()) return Preview(db, actor, id);
    }
    private SendPreview Preview(SqliteConnection db, OperatorActor actor, string id)
    {
        using var read = HubStore.Command(db, "SELECT mode,content,destinations,uploads,expires,state,discord,fingerprint FROM message_plans WHERE id=$0 AND actor=$1", id, Actor(actor)); using var r = read.ExecuteReader();
        if (!r.Read() || r.GetString(6) != actor.Discord) throw new HubError("Send plan unavailable.");
        var mode = r.GetString(0); var content = r.GetString(1); var destinations = SafeJson.Read<MessageDestination[]>(r.GetString(2), 512000); var ids = SafeJson.Read<string[]>(r.GetString(3), 4096); var expires = DateTimeOffset.Parse(r.GetString(4)); var state = r.GetString(5); var authorization = r.GetString(7); r.Close();
        if (mode is not ("normal" or "announcement") || content.Length > 2000 || destinations.Length > 1000 || ids.Length > 4) throw new HubError("Invalid retained message selection.");
        if (state != "retained") foreach (var destination in destinations) Target(destination.Target);
        IReadOnlyList<MessageUpload> files;
        if (state == "prepared") files = Files(db, actor, ids).Select(f => f.Meta).ToArray();
        else
        {
            var retained = new List<MessageUpload>();
            foreach (var key in ids) { using var file = HubStore.Command(db, "SELECT filename,LENGTH(body),mime FROM message_files WHERE id=$0 AND actor=$1 AND plan=$2 AND expires>$3", key, Actor(actor), id, Time(DateTimeOffset.UtcNow)); using var row = file.ExecuteReader(); if (row.Read()) retained.Add(new(key, row.GetString(0), row.GetInt64(1), row.GetString(2))); }
            files = retained;
        }
        var preview = new SendPreview(id, mode, content, destinations, files, expires, state);
        if (state != "retained")
        {
            // Expired retained blobs do not invalidate a read-only receipt; their manifest remains signed.
            var seal = SafeJson.Read<PlanSeal>(authorization, 8192);
            var manifest = seal.Files?.Select(f => new MessageUpload(f.Id, f.Filename, f.Size, f.Mime)).ToArray() ?? throw new HubError("Invalid message file manifest.");
            CheckPlan(actor, preview with { Files = manifest }, authorization);
            if (!ids.ToHashSet().SetEquals(manifest.Select(f => f.Id))) throw new HubError("Message attachment selection changed.");
        }
        else if (content.Length != 0 || destinations.Length != 0 || ids.Length != 0) throw new HubError("Invalid retained message tombstone.");
        return preview;
    }
    public async Task<SendPreview> GetPreview(OperatorActor actor, string id, CancellationToken cancellation)
    { await operators.Check(actor, cancellation); using var db = store.Open(); return Preview(db, actor, id); }
    public async Task<SendReceipt> Confirm(OperatorActor actor, string id, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation);
        SendPreview preview; using (var db = store.Open()) preview = Preview(db, actor, id);
        if (preview.State != "prepared") return await Receipt(actor, id, cancellation);
        if (preview.Expires <= DateTimeOffset.UtcNow) throw new HubError("Send preview expired. Preview again.");
        foreach (var destination in preview.Destinations) await discord.Verify(actor, destination.Target, true, preview.Files.Count > 0, cancellation);
        await operators.Check(actor, cancellation);
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            using var state = HubStore.Command(db, "SELECT state FROM message_plans WHERE id=$0 AND actor=$1", id, Actor(actor)); state.Transaction = tx;
            if (state.ExecuteScalar() as string != "prepared") { tx.Rollback(); return await Receipt(actor, id, cancellation); }
            var files = Files(db, actor, preview.Files.Select(f => f.Id).ToArray(), tx: tx); CheckSecrets(preview.Content, files.Select(f => f.Blob));
            using var sealRead = HubStore.Command(db, "SELECT fingerprint FROM message_plans WHERE id=$0", id); sealRead.Transaction = tx;
            var seal = CheckPlan(actor, preview, (string)sealRead.ExecuteScalar()!);
            if (seal.ConfigurationRevision != settings.Current.Revision) throw new HubError("Integration settings changed after preview. No send was authorized.");
            if (files.Any(f => !seal.Files.Any(p => p.Id == f.Meta.Id && p.Filename == f.Blob.Filename && p.Mime == f.Blob.Mime && p.Size == f.Blob.Bytes.Length && p.Digest == Convert.ToHexString(SHA256.HashData(f.Blob.Bytes))))) throw new HubError("Message attachment changed since preview.");
            using var claim = HubStore.Command(db, "UPDATE message_plans SET state='queued',fingerprint=$4 WHERE id=$0 AND actor=$1 AND discord=$2 AND state='prepared' AND expires>$3", id, Actor(actor), actor.Discord, Time(DateTimeOffset.UtcNow), SealPlan(actor, preview with { State = "queued" }, seal.Request, seal.Files, seal.ConfigurationRevision)); claim.Transaction = tx;
            if (claim.ExecuteNonQuery() == 1)
            {
                foreach (var file in preview.Files) { using var retain = HubStore.Command(db, "UPDATE message_files SET plan=$0,expires=$1 WHERE id=$2 AND actor=$3 AND plan IS NULL", id, Time(DateTimeOffset.UtcNow.AddDays(settings.Current.Features.MessageRetentionDays)), file.Id, Actor(actor)); retain.Transaction = tx; if (retain.ExecuteNonQuery() != 1) throw new HubError("Upload was already claimed."); }
                foreach (var destination in preview.Destinations)
                {
                    var delivery = HubStore.Nonce(); using var add = HubStore.Command(db, "INSERT INTO message_deliveries VALUES($0,$1,$2,'pending',NULL,$3)", delivery, id, SealDelivery(delivery, id, actor, "pending", destination), Time(DateTimeOffset.UtcNow)); add.Transaction = tx; add.ExecuteNonQuery();
                    using var inbox = HubStore.Command(db, "INSERT INTO message_inbox VALUES($0,'bot','Media Hub',$1,NULL,$2,$3,$4,$5,$6,NULL,0,'out',$7,$8)", "delivery:" + delivery, destination.Target.Kind == "channel" ? destination.Target.Guild : null, destination.Target.Kind == "channel" ? destination.Target.Id : "", destination.Label, preview.Content,
                        JsonSerializer.Serialize(new { attachments = preview.Files.Select(f => new { key = "upload:" + f.Id, filename = f.Filename, size = f.Size, image = f.Mime.StartsWith("image/") }) }), Time(DateTimeOffset.UtcNow), destination.Target.Kind == "user" ? destination.Target.Id : null, delivery); inbox.Transaction = tx; inbox.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        return await Receipt(actor, id, cancellation);
    }
    public async Task Cancel(OperatorActor actor, string id, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); using var db = store.Open(); var preview = Preview(db, actor, id);
        using var read = HubStore.Command(db, "SELECT fingerprint FROM message_plans WHERE id=$0", id); var encoded = (string)read.ExecuteScalar()!;
        var seal = CheckPlan(actor, preview, encoded);
        using var cancel = HubStore.Command(db, "UPDATE message_plans SET state='cancelled',fingerprint=$3 WHERE id=$0 AND actor=$1 AND discord=$2 AND state='prepared' AND fingerprint=$4", id, Actor(actor), actor.Discord,
            SealPlan(actor, preview with { State = "cancelled" }, seal.Request, seal.Files, seal.ConfigurationRevision), encoded);
        if (cancel.ExecuteNonQuery() != 1) throw new HubError("Only an unsent preview can be cancelled.");
    }
    public async Task<SendReceipt> Receipt(OperatorActor actor, string id, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); string mode, content; DateTimeOffset at; var deliveries = new List<MessageDelivery>();
        using (var db = store.Open())
        {
            var verifiedPlan = Preview(db, actor, id);
            using var plan = HubStore.Command(db, "SELECT mode,content,created FROM message_plans WHERE id=$0 AND actor=$1 AND discord=$2", id, Actor(actor), actor.Discord); using (var r = plan.ExecuteReader()) { if (!r.Read()) throw new HubError("Send receipt unavailable."); mode = r.GetString(0); content = r.GetString(1); at = DateTimeOffset.Parse(r.GetString(2)); }
            using var read = HubStore.Command(db, "SELECT id,target,state,message_id,updated FROM message_deliveries WHERE plan=$0 ORDER BY id LIMIT 1000", id); using var rows = read.ExecuteReader();
            while (rows.Read())
            { var state = rows.GetString(2); var destination = verifiedPlan.State == "retained" ? SafeJson.Read<MessageDestination>(rows.GetString(1), 4096) : CheckDelivery(rows.GetString(1), rows.GetString(0), id, actor, state);
                if (verifiedPlan.State == "retained" && destination.Target.Kind != "expired") throw new HubError("Invalid delivery tombstone.");
                if (state == "sending" && DateTimeOffset.Parse(rows.GetString(4)) < DateTimeOffset.UtcNow.AddMinutes(-1)) state = "uncertain";
                deliveries.Add(new(rows.GetString(0), state, rows.IsDBNull(3) ? null : rows.GetString(3), destination.Label)); }
        }
        return new(id, mode, content, deliveries, at);
    }
    public async Task<IReadOnlyList<SendReceipt>> History(OperatorActor actor, int page, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); if (page is < 1 or > 10000) throw new HubError("Invalid history page."); var ids = new List<string>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT id FROM message_plans WHERE actor=$0 AND discord=$1 AND state!='prepared' ORDER BY created DESC LIMIT 20 OFFSET $2", Actor(actor), actor.Discord, (page - 1) * 20)) using (var r = read.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        var result = new List<SendReceipt>(); foreach (var id in ids) result.Add(await Receipt(actor, id, cancellation)); return result;
    }
    public async Task Deliver(CancellationToken cancellation)
    {
        if (!settings.Current.DiscordEnabled) return;
        using (var db = store.Open()) using (var tx = db.BeginTransaction()) { Prune(db, tx); tx.Commit(); }
        var work = new List<(string Id, string Plan, OperatorActor Actor, string Content, string Destination, string[] Uploads)>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT d.id,d.plan,p.actor,p.discord,p.content,d.target,p.uploads FROM message_deliveries d JOIN message_plans p ON p.id=d.plan WHERE d.state='pending' AND p.state='queued' ORDER BY p.created,d.id LIMIT 10")) using (var r = read.ExecuteReader())
            while (r.Read()) work.Add((r.GetString(0), r.GetString(1), new(Guid.Parse(r.GetString(2)), r.GetString(3)), r.GetString(4), r.GetString(5), SafeJson.Read<string[]>(r.GetString(6), 4096)));
        foreach (var item in work)
        {
            cancellation.ThrowIfCancellationRequested(); List<(MessageUpload Meta, MessageBlob Blob)> files; MessageDestination destination; string planSignature;
            try
            {
                await operators.Check(item.Actor, cancellation); if (!settings.Current.DiscordEnabled) return;
                destination = CheckDelivery(item.Destination, item.Id, item.Plan, item.Actor, "pending");
                if (item.Content.Length > 2000) throw new HubError("Invalid retained message content.");
                await discord.Verify(item.Actor, destination.Target, true, item.Uploads.Length > 0, cancellation);
                using var db = store.Open(); var preview = Preview(db, item.Actor, item.Plan);
                if (preview.State != "queued" || preview.Content != item.Content || !preview.Destinations.Any(d => d.Target == destination.Target)) throw new HubError("Delivery selection changed after confirmation.");
                using var authorization = HubStore.Command(db, "SELECT fingerprint FROM message_plans WHERE id=$0", item.Plan); planSignature = (string)authorization.ExecuteScalar()!;
                var seal = SafeJson.Read<PlanSeal>(planSignature, 8192);
                if (seal.ConfigurationRevision != settings.Current.Revision) throw new HubError("Integration settings changed after confirmation. Pending delivery is blocked.");
                files = Files(db, item.Actor, item.Uploads, item.Plan); CheckSecrets(item.Content, files.Select(f => f.Blob));
                if (!files.Select(f => f.Meta.Id).ToHashSet().SetEquals(seal.Files.Select(f => f.Id)) || files.Any(f => !seal.Files.Any(p => p.Id == f.Meta.Id && p.Filename == f.Blob.Filename && p.Mime == f.Blob.Mime && p.Size == f.Blob.Bytes.Length && p.Digest == Convert.ToHexString(SHA256.HashData(f.Blob.Bytes))))) throw new HubError("Message attachment changed after confirmation.");
                await operators.Check(item.Actor, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { SetDelivery(item.Id, "blocked", null); continue; }
            using (var db = store.Open()) using (var claim = HubStore.Command(db, "UPDATE message_deliveries SET state='sending',updated=$0,target=$2 WHERE id=$1 AND state='pending' AND target=$3 AND EXISTS(SELECT 1 FROM message_plans p WHERE p.id=$4 AND p.state='queued' AND p.fingerprint=$5)", Time(DateTimeOffset.UtcNow), item.Id,
                SealDelivery(item.Id, item.Plan, item.Actor, "sending", destination), item.Destination, item.Plan, planSignature)) if (claim.ExecuteNonQuery() != 1) continue;
            var state = "uncertain"; string? message = null;
            try { message = await discord.Send(destination.Target, item.Content, files.Select(f => f.Blob).ToArray(), cancellation); Snowflake(message); state = "sent"; }
            catch { /* Includes cancellation after claim: never resend an uncertain Discord send. */ }
            SetDelivery(item.Id, state, message); cancellation.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
        }
    }
    private void SetDelivery(string id, string state, string? message)
    {
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var read = HubStore.Command(db, "SELECT d.plan,d.target,p.actor,p.discord FROM message_deliveries d JOIN message_plans p ON p.id=d.plan WHERE d.id=$0 AND d.state=$1", id, state == "blocked" ? "pending" : "sending"); read.Transaction = tx;
        string plan, target; OperatorActor actor;
        using (var r = read.ExecuteReader()) { if (!r.Read()) return; plan = r.GetString(0); target = r.GetString(1); actor = new(Guid.Parse(r.GetString(2)), r.GetString(3)); }
        try { var destination = CheckDelivery(target, id, plan, actor, state == "blocked" ? "pending" : "sending"); target = SealDelivery(id, plan, actor, state, destination); }
        catch (HubError) when (state == "blocked") { /* Quarantine invalid data locally, never manufacture authorization. */ }
        using var update = HubStore.Command(db, "UPDATE message_deliveries SET state=$0,message_id=$1,updated=$2,target=$5 WHERE id=$3 AND state=$4", state, message, Time(DateTimeOffset.UtcNow), id, state == "blocked" ? "pending" : "sending", target); update.Transaction = tx;
        if (update.ExecuteNonQuery() == 1 && state == "sent") { using var rename = HubStore.Command(db, "UPDATE message_inbox SET id=$0 WHERE delivery=$1", message, id); rename.Transaction = tx; rename.ExecuteNonQuery(); }
        tx.Commit();
    }
    public void Receive(MessageView message, JsonNode retainedRich, bool direct, bool mentioned, bool reply)
    {
        var s = settings.Current; if (!s.DiscordEnabled || s.Features.Operators.Length == 0 && !s.Features.SeerrOperators) return;
        Snowflake(message.Id); Snowflake(message.Author); Snowflake(message.Channel);
        if (!direct && (message.Guild is null || !s.AllowedGuilds.Contains(message.Guild))) return;
        using var db = store.Open(); using var tx = db.BeginTransaction();
        using var outgoing = HubStore.Command(db, "SELECT 1 FROM message_inbox WHERE channel=$0 AND guild=$1 AND direction='out' AND created>$2 LIMIT 1", message.Channel, message.Guild, Time(DateTimeOffset.UtcNow.AddDays(-s.Features.MessageRetentionDays))); outgoing.Transaction = tx;
        if (!direct && !mentioned && !reply && !s.Features.InboxChannels.Contains(message.Channel) && outgoing.ExecuteScalar() is null) return;
        if (retainedRich.ToJsonString().Length > 128000) throw new HubError("Message metadata is too large.");
        Prune(db, tx);
        using var add = HubStore.Command(db, "INSERT OR IGNORE INTO message_inbox VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,NULL,0,'in',$10,NULL)", message.Id, message.Author, message.AuthorName[..Math.Min(message.AuthorName.Length, 160)], message.Guild, message.GuildName, message.Channel, message.ChannelName,
            message.Content[..Math.Min(message.Content.Length, 4000)], retainedRich.ToJsonString(), Time(message.Created), direct ? message.Author : null); add.Transaction = tx; add.ExecuteNonQuery(); tx.Commit();
    }
    public void Edit(string id, string channel, string content, JsonNode rich)
    {
        if (rich.ToJsonString().Length > 128000) return; using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE message_inbox SET content=$0,rich=$1,edited=$2 WHERE id=$3 AND channel=$4 AND deleted=0", content[..Math.Min(content.Length, 4000)], rich.ToJsonString(), Time(DateTimeOffset.UtcNow), id, channel); update.ExecuteNonQuery();
    }
    public void Delete(string id, string channel)
    { using var db = store.Open(); using var update = HubStore.Command(db, "UPDATE message_inbox SET content='',rich='{}',deleted=1 WHERE id=$0 AND channel=$1", id, channel); update.ExecuteNonQuery(); }
    private async Task<bool> Readable(OperatorActor actor, string? guild, string channel, string? peer, CancellationToken cancellation)
    {
        if (guild is null) return true; // Explicit operator authorization includes the bot's private DM inbox, not arbitrary Discord DMs.
        try { await discord.Verify(actor, new("channel", channel, guild), false, false, cancellation); return true; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public async Task<InboxPage> Inbox(OperatorActor actor, string kind, string? guild, string? peer, string? channel, string query, int page, bool oldest, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation);
        if (kind is not ("all" or "dm" or "guild") || query.Length > 100 || page is < 1 or > 10000) throw new HubError("Invalid inbox filter.");
        foreach (var id in new[] { guild, peer, channel }.Where(v => v is not null)) Snowflake(id!);
        var rows = new List<MessageView>();
        using (var db = store.Open()) using (var tx = db.BeginTransaction())
        {
            Prune(db, tx);
            using var read = HubStore.Command(db, "SELECT i.*,d.state FROM message_inbox i LEFT JOIN message_deliveries d ON d.id=i.delivery WHERE ($0='all' OR ($0='dm' AND i.guild IS NULL) OR ($0='guild' AND i.guild IS NOT NULL)) AND ($1 IS NULL OR i.guild=$1) AND ($2 IS NULL OR i.author=$2 OR i.peer=$2) AND ($3 IS NULL OR i.channel=$3) AND (INSTR(LOWER(i.content),LOWER($4))>0 OR INSTR(LOWER(i.author_name),LOWER($4))>0) ORDER BY i.created " + (oldest ? "ASC" : "DESC") + ",i.id LIMIT 51 OFFSET $5", kind, guild, peer, channel, query, (page - 1) * 50); read.Transaction = tx;
            using (var r = read.ExecuteReader()) while (r.Read()) rows.Add(Project(r)); tx.Commit();
        }
        var visible = new List<MessageView>(); var checkedChannels = new Dictionary<string, bool>();
        foreach (var row in rows.Take(50))
        {
            var key = row.Guild + ":" + row.Channel;
            if (!checkedChannels.TryGetValue(key, out var allowed)) checkedChannels[key] = allowed = await Readable(actor, row.Guild, row.Channel, row.Peer, cancellation);
            if (allowed) visible.Add(row);
        }
        await operators.Check(actor, cancellation); return new(visible, page, rows.Count > 50);
    }
    private static MessageView Project(SqliteDataReader r)
    {
        var rich = SafeJson.Parse(r.GetString(8), 512000); rich.AsObject().Remove("assets");
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), rich,
            DateTimeOffset.Parse(r.GetString(9)), r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10)), r.GetInt32(11) != 0, r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(15) ? null : r.GetString(15));
    }
    public async Task<MessageView> Message(OperatorActor actor, string id, CancellationToken cancellation)
    {
        Snowflake(id); await operators.Check(actor, cancellation); MessageView row;
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT i.*,d.state FROM message_inbox i LEFT JOIN message_deliveries d ON d.id=i.delivery WHERE i.id=$0 AND i.created>=$1", id, Time(DateTimeOffset.UtcNow.AddDays(-settings.Current.Features.MessageRetentionDays))))
        using (var r = read.ExecuteReader()) { if (!r.Read()) throw new HubError("Referenced message is not retained."); row = Project(r); }
        if (!await Readable(actor, row.Guild, row.Channel, row.Peer, cancellation)) throw new HubError("Conversation access unavailable.");
        await operators.Check(actor, cancellation); return row;
    }
    public async Task<IReadOnlyDictionary<string, long>> Diagnostics(OperatorActor actor, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); var result = new Dictionary<string, long>();
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT d.state,COUNT(*) FROM message_deliveries d JOIN message_plans p ON p.id=d.plan WHERE p.actor=$0 AND p.discord=$1 GROUP BY d.state", Actor(actor), actor.Discord))
        using (var r = read.ExecuteReader()) while (r.Read()) { var state = r.GetString(0); if (state is not ("pending" or "sending" or "sent" or "uncertain" or "blocked")) throw new HubError("Invalid retained delivery state."); result.Add(state, r.GetInt64(1)); }
        await operators.Check(actor, cancellation); return result;
    }
    internal async Task<(string Url, string Filename, string Revision)> Asset(OperatorActor actor, string message, string key, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); string? guild, peer; string channel, rich;
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT guild,channel,peer,rich FROM message_inbox WHERE id=$0 AND deleted=0 AND created>=$1", message, Time(DateTimeOffset.UtcNow.AddDays(-settings.Current.Features.MessageRetentionDays)))) using (var r = read.ExecuteReader())
        { if (!r.Read()) throw new HubError("Attachment expired or was removed."); guild = r.IsDBNull(0) ? null : r.GetString(0); channel = r.GetString(1); peer = r.IsDBNull(2) ? null : r.GetString(2); rich = r.GetString(3); }
        if (!await Readable(actor, guild, channel, peer, cancellation)) throw new HubError("Conversation access is unavailable.");
        var data = SafeJson.Parse(rich, 512000)["assets"]?[key] ?? throw new HubError("Attachment unavailable.");
        return (data["url"]!.GetValue<string>(), Filename(data["filename"]?.GetValue<string>() ?? "attachment"), rich);
    }
    internal async Task<MessageBlob> SentUpload(OperatorActor actor, string message, string file, CancellationToken cancellation)
    {
        await operators.Check(actor, cancellation); string? guild; string channel; MessageBlob blob;
        using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT i.guild,i.channel,f.filename,f.mime,CASE WHEN LENGTH(f.body) BETWEEN 1 AND 8388608 THEN f.body ELSE NULL END FROM message_inbox i JOIN message_deliveries d ON d.id=i.delivery JOIN message_files f ON f.plan=d.plan WHERE i.id=$0 AND i.deleted=0 AND f.id=$1 AND f.expires>$2", message, file, Time(DateTimeOffset.UtcNow))) using (var r = read.ExecuteReader())
        { if (!r.Read() || r.IsDBNull(4)) throw new HubError("Attachment expired, exceeds its size limit, or was removed."); guild = r.IsDBNull(0) ? null : r.GetString(0); channel = r.GetString(1); var body = (byte[])r[4]; blob = new(body, Filename(r.GetString(2)), Mime(body)); }
        if (!await Readable(actor, guild, channel, null, cancellation)) throw new HubError("Conversation access unavailable."); await operators.Check(actor, cancellation); return blob;
    }
}
