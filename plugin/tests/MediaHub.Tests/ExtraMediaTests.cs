using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class ExtraMediaTests
{
    private const string Album = "11111111-2222-3333-4444-555555555555";
    [Theory]
    [InlineData("music", Album)] [InlineData("book", "123")]
    public async Task OnlySelectedItemIsAddedWithVerifiedProfilesAndNoParentWideSearch(string kind, string id)
    {
        using var f = new FeatureFixture(); var server = new Server(); using var http = new ServiceHttp(server); var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        var search = await service.Search(f.User, kind, "query", 1, CancellationToken.None); Assert.Single(search.Items);
        Assert.Equal(0, server.Posts); f.Data.VerifyNoOtherCalls();
        var plan = await service.Prepare(f.User, kind, id, kind == "book" ? "456" : null, CancellationToken.None);
        Assert.Equal(0, server.Posts);
        var result = await service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("completed", result.State); Assert.Equal(33, result.RequestId); Assert.Equal(1, server.Posts);
        await service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None); Assert.Equal(1, server.Posts);
        var posted = Assert.Single(server.Bodies); Assert.DoesNotContain("/injected", posted); Assert.DoesNotContain("searchForMissingAlbums\":true", posted); Assert.DoesNotContain("searchForMissingBooks\":true", posted); Assert.DoesNotContain("\"path\"", posted);
        var json = JsonNode.Parse(posted)!; var parent = json[kind == "music" ? "artist" : "author"]!;
        Assert.Equal("/media", parent["rootFolderPath"]!.GetValue<string>()); Assert.Equal(2, parent["qualityProfileId"]!.GetValue<int>()); Assert.Equal(3, parent["metadataProfileId"]!.GetValue<int>()); Assert.Equal("none", parent["monitorNewItems"]!.GetValue<string>());
        Assert.All(server.Methods, method => Assert.Contains(method, new[] { HttpMethod.Get, HttpMethod.Post }));
    }
    [Theory]
    [InlineData("excluded")] [InlineData("profile")] [InlineData("existing")] [InlineData("parent")]
    public async Task ChangedOrUnsafeTargetsBlockWithoutAnyExternalMutation(string problem)
    {
        using var f = new FeatureFixture(); var server = new Server(); using var http = new ServiceHttp(server); var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        var plan = await service.Prepare(f.User, "music", Album, null, CancellationToken.None);
        server.Excluded = problem == "excluded"; server.BadProfile = problem == "profile"; server.Existing = problem == "existing"; server.ChangedParent = problem == "parent";
        await Assert.ThrowsAsync<HubError>(() => service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None)); Assert.Equal(0, server.Posts);
    }
    [Fact]
    public async Task DailyLimitsAreAtomicAndUncertainAddsNeverReplayAfterRestart()
    {
        using var f = new FeatureFixture(); f.Update(f.Features(limit: 1)); var server = new Server { LoseResponse = true }; using var http = new ServiceHttp(server);
        var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        var plan = await service.Prepare(f.User, "music", Album, null, CancellationToken.None);
        Assert.Equal("uncertain", (await service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None)).State);
        await new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity).Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None); Assert.Equal(1, server.Posts);
        var next = await service.Prepare(f.User, "music", "21111111-2222-3333-4444-555555555555", null, CancellationToken.None);
        await Assert.ThrowsAsync<HubError>(() => service.Confirm(f.User, next.Id, () => Task.CompletedTask, CancellationToken.None)); Assert.Equal(1, server.Posts);
    }
    [Fact]
    public async Task NativeVisibilityAndLiveSeerrPermissionsApplyToMusicAndBooks()
    {
        using var f = new FeatureFixture(); var server = new Server(); using var http = new ServiceHttp(server); var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        var album = new Mock<MusicAlbum>(); album.Object.Id = Guid.NewGuid(); album.Object.ProviderIds["MusicBrainzReleaseGroup"] = Album; album.Setup(a => a.IsVisibleStandalone(f.User)).Returns(false);
        f.Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([album.Object]);
        Assert.Empty((await service.Search(f.User, "music", "query", 1, CancellationToken.None)).Items);
        await Assert.ThrowsAsync<HubError>(() => service.Prepare(f.User, "music", Album, null, CancellationToken.None));
        f.Library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var plan = await service.Prepare(f.User, "music", Album, null, CancellationToken.None); f.Permissions = 0;
        await Assert.ThrowsAsync<HubError>(() => service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None)); Assert.Equal(0, server.Posts); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task OwnLocalExtraWatchlistRevisionsMuteAndRemovalDoNotSendArrWrites()
    {
        using var f = new FeatureFixture(); var server = new Server(); using var http = new ServiceHttp(server); var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        await service.Follow(f.User, "book", "123", true, 0, () => Task.CompletedTask, CancellationToken.None); Assert.True(Assert.Single(service.Watches(f.User)).Subscribed);
        await service.Follow(f.User, "book", "123", false, 1, () => Task.CompletedTask, CancellationToken.None); Assert.False(Assert.Single(service.Watches(f.User)).Subscribed);
        await Assert.ThrowsAsync<HubError>(() => service.Follow(f.User, "book", "123", null, 1, () => Task.CompletedTask, CancellationToken.None)); Assert.Single(service.Watches(f.User));
        await service.Follow(f.User, "book", "123", null, 2, () => Task.CompletedTask, CancellationToken.None); Assert.Empty(service.Watches(f.User)); Assert.Equal(0, server.Posts); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task ServiceSettingsAreEncryptedAndChangesInvalidatePreparedRequests()
    {
        using var f = new FeatureFixture(); var disk = File.ReadAllText(Path.Combine(f.Directory, "settings.json")); Assert.DoesNotContain("lidarr-private-key", disk); Assert.DoesNotContain("readarr-private-key", JsonSerializer.Serialize(f.Settings.Public()));
        var server = new Server(); using var http = new ServiceHttp(server); var service = new ExtraMedia(f.Store, f.Settings, f.Bridge, http, f.Library.Object, f.Activity);
        var plan = await service.Prepare(f.User, "music", Album, null, CancellationToken.None);
        var update = f.Features(); f.Update(update with { Lidarr = update.Lidarr with { Root = "/other" } });
        await Assert.ThrowsAsync<HubError>(() => service.Confirm(f.User, plan.Id, () => Task.CompletedTask, CancellationToken.None)); Assert.Equal(0, server.Posts);
    }
    private sealed class Server : HttpMessageHandler
    {
        public bool Excluded, BadProfile, Existing, ChangedParent, LoseResponse;
        public int Posts; public readonly List<string> Bodies = []; public readonly List<HttpMethod> Methods = []; private readonly Dictionary<string, string> added = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method); var music = request.RequestUri!.Host == "lidarr"; var kind = music ? "album" : "book"; var foreign = music ? "foreignAlbumId" : "foreignBookId";
            Assert.Equal(music ? "lidarr-private-key" : "readarr-private-key", request.Headers.GetValues("X-Api-Key").Single());
            var path = request.RequestUri.AbsolutePath["/api/v1/".Length..]; object body;
            var term = Uri.UnescapeDataString(request.RequestUri.Query.Replace("?term=", "")); var id = term.Contains(':') ? term[(term.IndexOf(':') + 1)..] : music ? Album : "123";
            object Row(string eid, int native = 0) => music ? new { id = native, foreignAlbumId = eid, title = "Album", path = "/injected", artist = new { foreignArtistId = ChangedParent ? "other-parent" : "original-parent", artistName = "Artist", path = "/injected", qualityProfileId = 999 }, overview = "<script>untrusted</script>" } :
                (object)new { id = native, foreignBookId = eid, title = "Book", path = "/injected", author = new { foreignAuthorId = "author", authorName = "Author" }, editions = new[] { new { foreignEditionId = "456" } } };
            if (request.Method == HttpMethod.Post)
            {
                Posts++; var text = await request.Content!.ReadAsStringAsync(cancellationToken); Bodies.Add(text); var json = JsonNode.Parse(text)!; var eid = json[foreign]!.GetValue<string>(); added[eid] = eid;
                if (LoseResponse) throw new HttpRequestException("Applied but no response");
                body = new JsonObject { ["id"] = 33, [foreign] = eid };
            }
            else if (path.EndsWith("/lookup")) body = new[] { Row(id, Existing ? 99 : 0) };
            else if (path == "rootfolder") body = new[] { new { path = "/media", accessible = true } };
            else if (path == "qualityprofile") body = new[] { new { id = BadProfile ? 999 : 2 } };
            else if (path == "metadataprofile") body = new[] { new { id = 3 } };
            else if (path == "importlistexclusion") body = Excluded ? [new { foreignId = "original-parent" }] : Array.Empty<object>();
            else if (path == kind) body = added.Keys.Select(eid => Row(eid, 33)).Concat(Existing ? [Row(id, 99)] : Array.Empty<object>()).ToArray();
            else throw new InvalidOperationException("Unexpected route: " + path);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
}
