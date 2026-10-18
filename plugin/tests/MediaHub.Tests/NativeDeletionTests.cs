using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class NativeDeletionTests
{
    private static void Configure(FeatureFixture f, DeletionScope scope, bool second = false)
    {
        var s = f.Settings.Current;
        f.Settings.Update(new(s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, "seerr-key", AutomaticDeletion: true,
            Features: f.Features() with { ArrDestinations = second ? [new("radarr", 0, "http://arr:7878"), new("radarr", 1, "http://arr2:7878")] : [new("radarr", 0, "http://arr:7878"), new("sonarr", 0, "http://arr:7878")] },
            DeletionPermissions: new(scope)));
    }
    private static Guid Library(FeatureFixture f, bool tv, Func<bool>? visible = null)
    {
        BaseItem value;
        if (tv) { var item = new Mock<Series>(); item.SetupProperty(m => m.Path); item.Setup(m => m.IsVisibleStandalone(f.User)).Returns(() => visible?.Invoke() ?? true); value = item.Object; }
        else { var item = new Mock<Movie>(); item.SetupProperty(m => m.Path); item.Setup(m => m.IsVisibleStandalone(f.User)).Returns(() => visible?.Invoke() ?? true); value = item.Object; }
        value.Id = Guid.NewGuid(); value.Path = tv ? "/tv/show" : "/movies/title/file.mkv";
        value.ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "123" }; if (tv) value.ProviderIds["Tvdb"] = "444";
        f.Library.Setup(l => l.GetItemById(value.Id)).Returns(value); return value.Id;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AnyItemUsesVerifiedNativeSourceAndNeverFabricatesSeerrRequests(bool tv)
    {
        using var f = new FeatureFixture(); Configure(f, DeletionScope.AnyAccessibleItem); var item = Library(f, tv);
        var server = new ArrDeletionTests.ArrServer(f.User.Id, tv);
        using var arr = new ArrDeletion(f.Settings, server, new(f.Users.Object, f.Library.Object, new(f.Settings)));
        var target = await arr.Discover(f.User, item, default); Assert.Equal(item, target.Item);
        Assert.Equal(0, target.Arr!.RequestId); Assert.Equal(0, target.Arr.MediaId); Assert.Equal(f.User.Id, target.Arr.Native!.User);
        var proof = await arr.Capture(f.User, target, default); await arr.Delete(await arr.Prepare(f.User, proof, default), default);
        Assert.True(await arr.Gone(f.User, proof, default)); await arr.Unmonitor(f.User, proof, default);
        Assert.Equal(0, server.RequestReads); Assert.Equal(1, server.Deletes); Assert.False(server.Monitored);
    }
    [Fact]
    public async Task OwnRequestsDefaultAndAdministratorStatusDoNotAuthorizeNativeTargets()
    {
        using var f = new FeatureFixture(); var item = Library(f, false); f.User.SetPermission(PermissionKind.IsAdministrator, true);
        Assert.Equal(DeletionScope.OwnRequests, new DeletionPermissions(f.Settings).Scope(f.User));
        using var arr = new ArrDeletion(f.Settings, new ArrDeletionTests.ArrServer(f.User.Id, false), new(f.Users.Object, f.Library.Object, new(f.Settings)));
        await Assert.ThrowsAsync<HubError>(() => arr.Discover(f.User, item, default));
        using var authority = new RequestAuthority(f.Settings, f.Library.Object, new ArrDeletionTests.ArrServer(f.User.Id, false), arr);
        await Assert.ThrowsAsync<HubError>(() => authority.Verify(f.User, 0, item, default));
        Assert.DoesNotContain("DeletionPermissions", JsonSerializer.Serialize(new HubSettings()));
        Assert.DoesNotContain("\"Native\":", JsonSerializer.Serialize(new ArrRequest("http://seerr", 22, 2, "movie", false, 0, 123, 123, "/movies/title/file.mkv")));
    }
    [Theory]
    [InlineData("permission")] [InlineData("disabled")] [InlineData("visibility")] [InlineData("file")]
    public async Task RevocationAndReplacementBlockPreparedNativeDeletion(string change)
    {
        using var f = new FeatureFixture(); Configure(f, DeletionScope.AnyAccessibleItem); var visible = true; var item = Library(f, false, () => visible);
        var server = new ArrDeletionTests.ArrServer(f.User.Id, false); using var arr = new ArrDeletion(f.Settings, server, new(f.Users.Object, f.Library.Object, new(f.Settings)));
        var proof = await arr.Capture(f.User, await arr.Discover(f.User, item, default), default); var prepared = await arr.Prepare(f.User, proof, default);
        if (change == "permission") Configure(f, DeletionScope.OwnRequests);
        if (change == "disabled") f.User.SetPermission(PermissionKind.IsDisabled, true);
        if (change == "visibility") visible = false;
        if (change == "file") { server.FileId++; await Assert.ThrowsAsync<HubError>(() => arr.Prepare(f.User, proof, default)); return; }
        await Assert.ThrowsAsync<HubError>(() => arr.Delete(prepared, default)); Assert.Equal(0, server.Deletes);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MultipleMatchesAndIncompleteDestinationReadsNeverSelectTheFirstCandidate(bool outage)
    {
        using var f = new FeatureFixture(); Configure(f, DeletionScope.AnyAccessibleItem, second: true); var item = Library(f, false);
        var server = new ArrDeletionTests.ArrServer(f.User.Id, false) { SecondDestination = true, FailSecondDestination = outage };
        using var arr = new ArrDeletion(f.Settings, server, new(f.Users.Object, f.Library.Object, new(f.Settings)));
        await Assert.ThrowsAnyAsync<Exception>(() => arr.Discover(f.User, item, default)); Assert.Equal(0, server.Deletes);
    }
    [Fact]
    public void OverridesAreExplicitBoundedAndRevocableAndRetainedSignaturesSurviveRestart()
    {
        using var f = new FeatureFixture(); Configure(f, DeletionScope.Denied); var s = f.Settings.Current;
        f.Settings.Update(new(s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null, DeletionPermissions: new(DeletionScope.Denied, [new(f.User.Id, DeletionScope.AnyAccessibleItem)])));
        Assert.Equal(DeletionScope.AnyAccessibleItem, new DeletionPermissions(new SettingsService(f.Store)).Scope(f.User));
        Assert.Throws<HubError>(() => DeletionPermissions.Validate(new(DeletionScope.OwnRequests, [new(f.User.Id, DeletionScope.AnyAccessibleItem), new(f.User.Id, DeletionScope.Denied)])));
        Configure(f, DeletionScope.Denied); Assert.Throws<HubError>(() => new DeletionPermissions(f.Settings).Require(f.User, false));
    }
    [Fact]
    public void OversizedCombinedSettingsCannotPublishAnUnreadableConfiguration()
    {
        using var f = new FeatureFixture(); var file = Path.Combine(f.Store.DirectoryPath, "settings.json"); var original = File.ReadAllBytes(file); var s = f.Settings.Current;
        var destinations = Enumerable.Range(0, 100).Select(id => new ArrDestination("radarr", id, "http://arr/" + new string('a', 1800))).ToArray();
        Assert.Throws<HubError>(() => f.Settings.Update(new(s.Revision, s.DiscordEnabled, s.PublicJellyfinUrl, s.AllowedGuilds, s.SeerrUrl, null, null, Features: f.Features() with { ArrDestinations = destinations })));
        Assert.Equal(original, File.ReadAllBytes(file)); Assert.Equal(s.Revision, new SettingsService(f.Store).Current.Revision);
    }
    [Fact]
    public void BoundedLargeDeletionProofsDoNotRelaxCredentialBudgetsOrChangeLegacyEncryption()
    {
        using var f = new FeatureFixture(); const string purpose = "MediaHub.Deletion.v1|proposal|actor|item|prepared";
        var legacy = f.Settings.Protect("retained proof", purpose); Assert.Equal("retained proof", f.Settings.UnprotectDeletionProof(legacy, purpose));
        var payload = new string('a', 131072); var cipher = f.Settings.ProtectDeletionProof(payload, purpose);
        Assert.Equal(payload, f.Settings.UnprotectDeletionProof(cipher, purpose));
        Assert.Throws<HubError>(() => f.Settings.Protect(payload)); Assert.Throws<HubError>(() => f.Settings.Unprotect(cipher));
        Assert.Throws<HubError>(() => f.Settings.ProtectDeletionProof(payload, "MediaHub.Link.v1|wrong"));
        Assert.Throws<HubError>(() => f.Settings.ProtectDeletionProof(new string('a', SettingsService.DeletionProofLimit + 1), purpose));
        Assert.ThrowsAny<Exception>(() => f.Settings.UnprotectDeletionProof(cipher, purpose + "changed"));
    }
}
