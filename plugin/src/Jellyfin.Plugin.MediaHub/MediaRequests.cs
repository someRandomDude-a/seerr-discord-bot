using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.MediaHub;

public sealed record RequestPlan(string Id, string Title, DateTimeOffset Expires);
public sealed record RequestReceipt(string Id, string State, int? RequestId, ActivityReceipt? Activity = null);
public sealed class MediaRequests(HubStore store, SeerrBridge bridge, MediaCatalog catalog, MediaActivity activity, SettingsService settings)
{
    private static string Purpose(string token, User user, string origin, int seerr, string kind, int external, string expires) => string.Join('|', "MediaHub.Request.v1", token, user.Id.ToString("N"), origin, seerr, kind, external, expires);
    public async Task<RequestPlan> Prepare(User user, string kind, int id, bool is4k, int[]? seasons, CancellationToken cancellation)
    {
        MediaCatalog.Kind(kind); if (id <= 0 || seasons is not null && (seasons.Length is 0 or > 100 || seasons.Any(s => s < 0) || seasons.Distinct().Count() != seasons.Length)) throw new HubError("Invalid request selection.");
        catalog.RequireAccess(user, kind, id);
        using var scope = await bridge.Open(user, cancellation);
        if (!(is4k ? scope.Can(kind == "movie" ? 1024 | 2048 : 1024 | 4096) : scope.Can(kind == "movie" ? 32 | 262144 : 32 | 524288))) throw new HubError("Seerr request permission is required.");
        var details = await scope.Get(kind + "/" + id, cancellation);
        if (details["id"]?.GetValue<int>() != id) throw new HubError("Upstream media identity did not match the selected request.");
        var body = new JsonObject { ["mediaType"] = kind, ["mediaId"] = id, ["is4k"] = is4k };
        if (kind == "tv")
        {
            if (seasons is not null)
            {
                var known = details["seasons"]!.AsArray().Select(s => s!["seasonNumber"]!.GetValue<int>()).ToHashSet();
                if (seasons.Any(s => !known.Contains(s))) throw new HubError("Season selection changed.");
                body["seasons"] = new JsonArray(seasons.Select(s => JsonValue.Create(s)).Cast<JsonNode?>().ToArray());
            }
            else body["seasons"] = "all";
        }
        var token = HubStore.Nonce(); var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        await scope.Verify(cancellation); catalog.RequireAccess(user, kind, id);
        using var db = store.Open(); using var tx = db.BeginTransaction();
        settings.Protection.Check(db, tx, "requests");
        using var expire = HubStore.Command(db, "UPDATE request_operations SET state='expired' WHERE user=$0 AND state='prepared' AND expires<=$1", user.Id.ToString("N"), DateTimeOffset.UtcNow.ToString("O")); expire.Transaction = tx; expire.ExecuteNonQuery();
        using var duplicate = HubStore.Command(db, "SELECT 1 FROM request_operations WHERE user=$0 AND origin=$1 AND seerr_id=$2 AND kind=$3 AND external_id=$4 AND state IN ('prepared','sending','uncertain')", user.Id.ToString("N"), scope.Viewer.Origin, scope.Viewer.Id, kind, id); duplicate.Transaction = tx;
        if (duplicate.ExecuteScalar() is not null) throw new HubError("A request is pending or unconfirmed. Check its status before trying again.");
        using var insert = HubStore.Command(db, "INSERT INTO request_operations VALUES($0,$1,$2,$3,$4,$5,$6,'prepared',NULL,$7)", token, user.Id.ToString("N"), scope.Viewer.Origin, scope.Viewer.Id, kind, id, settings.Protect(body.ToJsonString(), Purpose(token, user, scope.Viewer.Origin, scope.Viewer.Id, kind, id, expires.ToString("O"))), expires.ToString("O"));
        insert.Transaction = tx; insert.ExecuteNonQuery(); settings.Protection.Seal(db, tx, "requests"); tx.Commit();
        return new(token, details["title"]?.GetValue<string>() ?? details["name"]?.GetValue<string>() ?? "Untitled", expires);
    }
    public RequestReceipt Receipt(User user, string id)
    {
        settings.Protection.Check("requests");
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT state,result FROM request_operations WHERE id=$0 AND user=$1", id, user.Id.ToString("N"));
        using var r = read.ExecuteReader(); if (!r.Read()) throw new HubError("Request confirmation unavailable.");
        return new(id, r.GetString(0), r.IsDBNull(1) ? null : int.Parse(r.GetString(1)));
    }
    public RequestReceipt Cancel(User user, string id)
    {
        var changed = settings.Protection.Update("requests", (db, tx) => { using var cancel = HubStore.Command(db, "UPDATE request_operations SET state='cancelled' WHERE id=$0 AND user=$1 AND state='prepared'", id, user.Id.ToString("N")); cancel.Transaction = tx; return cancel.ExecuteNonQuery(); });
        if (changed != 1) throw new HubError("Only an unsent request plan can be cancelled. Check Seerr for sent or uncertain requests.");
        return Receipt(user, id);
    }
    public async Task<RequestReceipt> Confirm(User user, string id, Func<Task> verifyActor, CancellationToken cancellation)
    {
        settings.Protection.Check("requests");
        if (settings.Protection.LegacyRequest(id)) throw new HubError("Legacy request authorization is read-only. Inspect Seerr and prepare a new explicit selection; no retry was sent.");
        using var scope = await bridge.Open(user, cancellation);
        string payload, kind; int external;
        using (var db = store.Open())
        using (var read = HubStore.Command(db, "SELECT origin,seerr_id,kind,external_id,payload,state,expires FROM request_operations WHERE id=$0 AND user=$1", id, user.Id.ToString("N")))
        using (var r = read.ExecuteReader())
        {
            if (!r.Read() || r.GetString(0) != scope.Viewer.Origin || r.GetInt32(1) != scope.Viewer.Id) throw new HubError("Request context changed.");
            if (r.GetString(5) != "prepared") return Receipt(user, id);
            if (DateTimeOffset.Parse(r.GetString(6)) <= DateTimeOffset.UtcNow) throw new HubError("Request confirmation expired.");
            kind = r.GetString(2); external = r.GetInt32(3);
            payload = settings.Unprotect(r.GetString(4), Purpose(id, user, scope.Viewer.Origin, scope.Viewer.Id, kind, external, r.GetString(6)));
        }
        await verifyActor(); await scope.Verify(cancellation);
        catalog.RequireAccess(user, kind, external);
        var retained = SafeJson.Parse(payload, 4096).AsObject();
        if (retained.Any(p => p.Key is not ("mediaType" or "mediaId" or "is4k" or "seasons")) || retained["mediaType"]?.GetValue<string>() != kind || retained["mediaId"]?.GetValue<int>() != external)
            throw new HubError("Invalid retained request selection. Prepare a new request.");
        var body = new JsonObject { ["mediaType"] = kind, ["mediaId"] = external, ["is4k"] = retained["is4k"]!.GetValue<bool>() };
        if (kind == "tv")
        {
            var selected = retained["seasons"];
            if (selected is JsonValue all && all.TryGetValue<string>(out var text) && text == "all") body["seasons"] = "all";
            else
            {
                var seasons = selected?.AsArray().Select(n => n!.GetValue<int>()).ToArray() ?? throw new HubError("Missing season selection.");
                if (seasons.Length is 0 or > 100 || seasons.Any(n => n < 0) || seasons.Distinct().Count() != seasons.Length) throw new HubError("Invalid season selection.");
                body["seasons"] = new JsonArray(seasons.Select(n => JsonValue.Create(n)).Cast<JsonNode?>().ToArray());
            }
        }
        else if (retained.ContainsKey("seasons")) throw new HubError("A movie request cannot contain TV seasons.");
        if (!(body["is4k"]!.GetValue<bool>() ? scope.Can(kind == "movie" ? 1024 | 2048 : 1024 | 4096) : scope.Can(kind == "movie" ? 32 | 262144 : 32 | 524288))) throw new HubError("Seerr request permission was revoked.");
        // The server enforces current quotas, profiles, permissions and season availability via X-API-User.
        var claimed = settings.Protection.Update("requests", (db, tx) => { using var claim = HubStore.Command(db, "UPDATE request_operations SET state='sending',payload='' WHERE id=$0 AND user=$1 AND state='prepared' AND expires>$2", id, user.Id.ToString("N"), DateTimeOffset.UtcNow.ToString("O")); claim.Transaction = tx; return claim.ExecuteNonQuery(); });
        if (claimed != 1) return Receipt(user, id);
        string state = "uncertain"; int? requestId = null;
        try
        {
            var result = await scope.Write(HttpMethod.Post, "request", body, cancellation);
            if (result["requestedBy"]?["id"]?.GetValue<int>() != scope.Viewer.Id || result["type"]?.GetValue<string>() != kind || result["media"]?["tmdbId"]?.GetValue<int>() != external ||
                result["is4k"]?.GetValue<bool>() != body["is4k"]!.GetValue<bool>())
                throw new HubError("Request attribution was not confirmed.");
            requestId = result["id"]!.GetValue<int>(); if (requestId <= 0) throw new HubError("Invalid request receipt."); state = "completed";
        }
        catch { /* Uncertain writes are never automatically replayed, including server-side denial after claim. */ }
        settings.Protection.Update("requests", (db, tx) => { using var save = HubStore.Command(db, "UPDATE request_operations SET state=$0,result=$1 WHERE id=$2 AND state='sending'", state, requestId?.ToString(), id); save.Transaction = tx; return save.ExecuteNonQuery(); });
        ActivityReceipt? recorded = null;
        if (state == "completed")
            try { var matches = catalog.Matches(user, kind, external); if (matches.Count == 1) recorded = activity.Record(user, matches[0].Id, MediaInteraction.Request); }
            catch { recorded = new(false, false); /* Preserve the confirmed request and expose only the partial activity failure. */ }
        return Receipt(user, id) with { Activity = recorded };
    }
}
