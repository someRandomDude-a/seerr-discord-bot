using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class ArrDeletionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-arr-" + Guid.NewGuid());
    private readonly User owner = new("Owner", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly SettingsService settings;
    public ArrDeletionTests()
    {
        settings = new(new HubStore(directory));
        settings.Update(new(0, false, "", [], "http://seerr", null, "seerr-key", AutomaticDeletion: true,
            Features: new(new("", null, "", 0, 0, false), new("", null, "", 0, 0, false), [], ArrDestinations: [new("radarr", 0, "http://arr:7878"), new("sonarr", 0, "http://arr:7878")])));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeletesThroughArrFileApiThenUnmonitorsAllFlags(bool tv)
    {
        var server = new ArrServer(owner.Id, tv); using var client = new ArrDeletion(settings, server);
        var request = new ArrRequest("http://seerr", 22, 2, tv ? "tv" : "movie", false, 0, tv ? 444 : 123, 123, tv ? "/tv/show" : "/movies/title/file.mkv");
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", request), CancellationToken.None);
        await client.Delete(await client.Prepare(owner, proof, CancellationToken.None), CancellationToken.None);
        Assert.True(await client.Gone(owner, proof, CancellationToken.None));
        await client.Unmonitor(owner, proof, CancellationToken.None);
        await client.Unmonitor(owner, proof, CancellationToken.None); // Cleanup retry is idempotent, not another deletion.
        Assert.Equal(1, server.Deletes); Assert.False(server.Monitored);
        if (tv) { Assert.True(server.SeasonsOff); Assert.True(server.EpisodesOff); Assert.Equal("none", server.Future); }
        Assert.All(server.DeletePaths, path => Assert.EndsWith(tv ? "/episodefile/bulk" : "/moviefile/bulk", path));
    }
    [Fact]
    public async Task ReplacedFilesEntriesOrDestinationsBlockBeforeDeletion()
    {
        foreach (var change in new[] { "file", "entry", "destination", "path", "recreated" })
        {
            var server = new ArrServer(owner.Id, false); using var client = new ArrDeletion(settings, server);
            var request = new ArrRequest("http://seerr", 22, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv");
            var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", request), CancellationToken.None);
            if (change == "file") server.FileId++;
            if (change == "entry") server.EntryId++;
            if (change == "destination") server.Port++;
            if (change == "path") server.Path = "/other/movie";
            if (change == "recreated") server.Added = "2026-10-17T00:00:00Z";
            await Assert.ThrowsAsync<HubError>(() => client.Prepare(owner, proof, CancellationToken.None));
            Assert.Equal(0, server.Deletes);
        }
    }
    [Fact]
    public async Task AmbiguousArrResponseDoesNotCauseTransportRetry()
    {
        var server = new ArrServer(owner.Id, false); using var client = new ArrDeletion(settings, server);
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", new("http://seerr", 22, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv")), CancellationToken.None);
        var prepared = await client.Prepare(owner, proof, CancellationToken.None); server.FailAfterDelete = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.Delete(prepared, CancellationToken.None));
        Assert.Equal(1, server.Deletes); Assert.True(await client.Gone(owner, proof, CancellationToken.None));
    }
    [Fact]
    public async Task NewUnownedSeasonBlocksWholeShowDeletionBeforeAnyWrite()
    {
        var server = new ArrServer(owner.Id, true); using var client = new ArrDeletion(settings, server);
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", new("http://seerr", 22, 2, "tv", false, 0, 444, 123, "/tv/show")), CancellationToken.None);
        server.AdditionalSeason = true;
        await Assert.ThrowsAsync<HubError>(() => client.Prepare(owner, proof, CancellationToken.None));
        Assert.Equal(0, server.Deletes); Assert.Equal(0, server.Puts);
    }
    [Fact]
    public async Task CleanupAllowsClearedSeerrInstanceButNeverRecreatesAbsentEntry()
    {
        var server = new ArrServer(owner.Id, false); using var client = new ArrDeletion(settings, server);
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", new("http://seerr", 22, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv")), CancellationToken.None);
        server.Gone = true; server.ClearInstance = true; server.EntryMissing = true;
        Assert.True(await client.Gone(owner, proof, CancellationToken.None));
        await client.Unmonitor(owner, proof, CancellationToken.None); Assert.Equal(0, server.Puts); Assert.Equal(0, server.Deletes);
    }
    [Fact]
    public async Task AdministratorRevocationAfterPreparationPreventsTheActualDelete()
    {
        var server = new ArrServer(owner.Id, false); using var client = new ArrDeletion(settings, server);
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", new("http://seerr", 22, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv")), CancellationToken.None);
        var prepared = await client.Prepare(owner, proof, CancellationToken.None); var s = settings.Current;
        settings.Update(new(s.Revision, false, "", [], "http://seerr", null, null, AutomaticDeletion: false));
        await Assert.ThrowsAsync<HubError>(() => client.Delete(prepared, CancellationToken.None)); Assert.Equal(0, server.Deletes);
    }
    [Fact]
    public async Task InvalidEpisodeScopeBlocksAllMonitoringWritesNotJustTheLastOne()
    {
        var server = new ArrServer(owner.Id, true); using var client = new ArrDeletion(settings, server);
        var proof = await client.Capture(owner, new(Guid.NewGuid(), "pin", new("http://seerr", 22, 2, "tv", false, 0, 444, 123, "/tv/show")), CancellationToken.None);
        server.Gone = true; server.BadEpisodes = true;
        await Assert.ThrowsAsync<HubError>(() => client.Unmonitor(owner, proof, CancellationToken.None));
        Assert.Equal(0, server.Puts); Assert.Equal(0, server.Deletes);
    }
    internal sealed class ArrServer(Guid owner, bool tv) : HttpMessageHandler
    {
        public int Deletes, Puts, FileId = 5, EntryId = 10, Port = 7878;
        public int RequestReads;
        public bool SecondDestination, FailSecondDestination;
        public string Path = tv ? "/tv/show" : "/movies/title";
        public bool Gone, Monitored = true, SeasonsOff, EpisodesOff, FailAfterDelete, AdditionalSeason, ClearInstance, EntryMissing, BadEpisodes;
        public string Future = "all";
        public string Added = "2026-10-16T00:00:00Z";
        public readonly List<string> DeletePaths = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!; var path = url.AbsolutePath;
            if (path.StartsWith("/api/v1/request/", StringComparison.Ordinal)) RequestReads++;
            if (url.Host == "arr2" && FailSecondDestination) throw new HttpRequestException("Fixture destination unavailable");
            Assert.Equal(url.Host == "seerr" ? "seerr-key" : "arr-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            object body;
            if (path == "/api/v1/request/22") body = new { id = 22, status = 2, type = tv ? "tv" : "movie", is4k = false, requestedBy = new { id = 7 }, media = new { id = 2, tmdbId = 123, tvdbId = 444, serviceId = ClearInstance ? (int?)null : 0 }, seasons = new[] { new { seasonNumber = 1 } } };
            else if (path == "/api/v1/user/7") body = new { id = 7, jellyfinUserId = owner.ToString() };
            else if (path == "/api/v1/tv/123") body = new { externalIds = new { tvdbId = 444 } };
            else if (path.StartsWith("/api/v1/settings/")) body = SecondDestination ? [new { id = 0, hostname = "arr", port = Port, useSsl = false, baseUrl = "", apiKey = "arr-key", is4k = false, isDefault = false }, new { id = 1, hostname = "arr2", port = Port, useSsl = false, baseUrl = "", apiKey = "arr-key", is4k = false, isDefault = false }] : new[] { new { id = 0, hostname = "arr", port = Port, useSsl = false, baseUrl = "", apiKey = "arr-key", is4k = false, isDefault = false } };
            else if (request.Method == HttpMethod.Delete)
            {
                DeletePaths.Add(path); Deletes++;
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Assert.Equal(FileId, payload[tv ? "episodeFileIds" : "movieFileIds"]![0]!.GetValue<int>());
                Gone = true; if (FailAfterDelete) throw new HttpRequestException("Response lost after Arr applied removal"); body = new { };
            }
            else if (request.Method == HttpMethod.Put)
            {
                Puts++;
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Assert.DoesNotContain("path", payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("qualityProfileId", payload.ToJsonString()); Assert.DoesNotContain("tags", payload.ToJsonString());
                if (path.EndsWith("/episode/monitor")) { Assert.False(payload["monitored"]!.GetValue<bool>()); EpisodesOff = true; }
                else
                {
                    Assert.False(payload["monitored"]!.GetValue<bool>()); Monitored = false;
                    if (tv) { Assert.EndsWith("/series/editor", path); Future = payload["monitorNewItems"]!.GetValue<string>(); Assert.Equal(EntryId, payload["seriesIds"]![0]!.GetValue<int>()); }
                    else Assert.Equal(EntryId, payload["movieIds"]![0]!.GetValue<int>());
                }
                body = new { };
            }
            else if (request.Method == HttpMethod.Post && path.EndsWith("/seasonpass"))
            {
                Puts++; var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                Assert.Equal(EntryId, payload["series"]![0]!["id"]!.GetValue<int>()); Assert.Equal("none", payload["monitoringOptions"]!["monitor"]!.GetValue<string>());
                SeasonsOff = true; EpisodesOff = true; body = new { };
            }
            else if (path.EndsWith("/episode")) body = new[] { new { id = 101, seriesId = BadEpisodes ? -1 : EntryId, seasonNumber = 1, monitored = !EpisodesOff } };
            else if (path.EndsWith("file")) body = Gone ? Array.Empty<object>() : [new { id = FileId, movieId = EntryId, seriesId = EntryId, seasonNumber = 1, path = Path + (tv ? "/s1/e1.mkv" : "/file.mkv"), size = 100L, dateAdded = "2026-10-16T00:00:00Z" }];
            else body = EntryMissing ? Array.Empty<object>() : [new { id = EntryId, tmdbId = 123, tvdbId = 444, path = Path, added = Added, monitored = Monitored, monitorNewItems = Future,
                seasons = AdditionalSeason ? new[] { new { seasonNumber = 1, monitored = !SeasonsOff }, new { seasonNumber = 2, monitored = true } } : [new { seasonNumber = 1, monitored = !SeasonsOff }] }];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
