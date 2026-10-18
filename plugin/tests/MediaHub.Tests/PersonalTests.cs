using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class PersonalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-personal-tests-" + Guid.NewGuid());
    private readonly User owner = new("Owner", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly User other = new("Other", "provider", "reset") { Id = Guid.NewGuid() };
    private readonly Guid item = Guid.NewGuid();
    private readonly Mock<IViewerMedia> media = new();
    private readonly HubStore store;
    private readonly PersonalService personal;
    public PersonalTests()
    {
        store = new(directory); personal = new(store, media.Object);
        media.Setup(m => m.Find(It.IsAny<User>(), item)).Returns(new VisibleItem(item, "<script>private title</script>", "Movie"));
    }
    [Fact]
    public void NewUsersHaveSeparateDefaultsAndDiscordRequiresOptIn()
    {
        Assert.Equal(new PersonalPreferences(true, false, false), personal.State(owner).Preferences);
        var changed = personal.Save(owner, 0, new(false, true, true));
        Assert.Equal(1, changed.Revision); Assert.True(changed.Preferences.DiscordNotifications);
        Assert.Equal(new PersonalPreferences(), personal.State(other).Preferences);
        Assert.Equal(0, personal.State(other).Revision);
    }
    [Fact]
    public void BellOnSavesAndSubscribesOffKeepsMembershipRemoveIsSeparate()
    {
        var on = personal.Bell(owner, item, true, 0);
        Assert.True(Assert.Single(on.Watchlist).Subscribed);
        var off = personal.Bell(owner, item, false, on.Revision);
        Assert.False(Assert.Single(off.Watchlist).Subscribed);
        Assert.Empty(personal.State(other).Watchlist);
        Assert.Empty(personal.Remove(owner, item, off.Revision).Watchlist);
    }
    [Fact]
    public void AddToWatchlistDoesNotForceOrEraseTheIndependentNotificationChoice()
    {
        var saved = personal.Add(owner, item, 0); Assert.False(Assert.Single(saved.Watchlist).Subscribed);
        var subscribed = personal.Bell(owner, item, true, saved.Revision); Assert.True(Assert.Single(subscribed.Watchlist).Subscribed);
        var again = personal.Add(owner, item, subscribed.Revision); Assert.True(Assert.Single(again.Watchlist).Subscribed);
        Assert.Empty(personal.State(other).Watchlist);
    }
    [Fact]
    public void DisablingUnknownBellDoesNotCreateMembershipAndMuteDoesNotEraseIt()
    {
        var off = personal.Bell(owner, item, false, 0); Assert.Empty(off.Watchlist);
        var on = personal.Bell(owner, item, true, off.Revision);
        var muted = personal.Save(owner, on.Revision, new(Muted: true));
        Assert.True(Assert.Single(muted.Watchlist).Subscribed);
    }
    [Fact]
    public void StaleMutationsAreRolledBackWithoutErasingCurrentState()
    {
        var initial = personal.Bell(owner, item, true, 0);
        Assert.Throws<HubError>(() => personal.Save(owner, 0, new(Muted: true)));
        Assert.Throws<HubError>(() => personal.Bell(owner, item, false, 0));
        Assert.Throws<HubError>(() => personal.Remove(owner, item, 0));
        var current = personal.State(owner);
        Assert.Equal(initial, current with { Watchlist = initial.Watchlist });
        Assert.True(Assert.Single(current.Watchlist).Subscribed);
    }
    [Fact]
    public void UnauthorizedOrMissingMediaHasOneGenericErrorAndNoStateChange()
    {
        var missing = Guid.NewGuid();
        media.Setup(m => m.Find(owner, item)).Returns((VisibleItem?)null);
        var denied = Assert.Throws<HubError>(() => personal.Bell(owner, item, true, 0));
        var absent = Assert.Throws<HubError>(() => personal.Bell(owner, missing, true, 0));
        Assert.Equal(denied.Message, absent.Message); Assert.Equal(0, personal.State(owner).Revision);
    }
    [Fact]
    public void LibraryRevocationHidesMetadataAndIdsOnEveryReadButAllowsRemovingOwnMembership()
    {
        var on = personal.Bell(owner, item, true, 0);
        media.Setup(m => m.Find(owner, item)).Returns((VisibleItem?)null);
        var json = JsonSerializer.Serialize(personal.State(owner));
        Assert.DoesNotContain(item.ToString(), json); Assert.DoesNotContain("private title", json);
        Assert.Empty(personal.Remove(owner, item, on.Revision).Watchlist);
    }
    [Fact]
    public void NativeVisibilityIsCheckedBeforeReturningItemMetadata()
    {
        var library = new Mock<ILibraryManager>(); var movie = new Mock<Movie>();
        movie.SetupGet(m => m.Name).Returns("restricted");
        library.Setup(l => l.GetItemById(item)).Returns(movie.Object);
        movie.Setup(m => m.IsVisibleStandalone(owner)).Returns(false);
        var native = new JellyfinViewerMedia(library.Object);
        Assert.Null(native.Find(owner, item));
        movie.Verify(m => m.IsVisibleStandalone(owner), Times.Once);
        movie.Setup(m => m.IsVisibleStandalone(owner)).Returns(true);
        Assert.Equal("restricted", native.Find(owner, item)!.Title);
    }
    [Fact]
    public void PreferencesAndSubscriptionsSurvivePluginRestart()
    {
        personal.Bell(owner, item, true, 0); personal.Save(owner, 1, new(false, true, true));
        var reopened = new PersonalService(new HubStore(directory), media.Object).State(owner);
        Assert.Equal(2, reopened.Revision); Assert.True(reopened.Preferences.Muted);
        Assert.Single(reopened.Watchlist); Assert.Equal(HubStore.SchemaVersion, ReadSchema());
    }
    [Fact]
    public async Task ConcurrentMutationsUseOneRevisionAndDoNotLoseUpdates()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            try { personal.Save(owner, 0, new(Muted: i == 1)); return true; }
            catch (HubError) { return false; }
        })));
        Assert.Single(outcomes, ok => ok); Assert.Equal(1, personal.State(owner).Revision);
    }
    [Fact]
    public void WatchlistCapRollsBackRevisionAndMembershipTogether()
    {
        using (var db = store.Open())
        using (var transaction = db.BeginTransaction())
        {
            for (var i = 0; i < 200; i++)
            {
                using var add = HubStore.Command(db, "INSERT INTO subscriptions VALUES($0,$1,0,0)", owner.Id.ToString("N"), Guid.NewGuid().ToString("N"));
                add.Transaction = transaction; add.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        Assert.Throws<HubError>(() => personal.Bell(owner, item, true, 0));
        Assert.Equal(0, personal.State(owner).Revision);
        using var check = store.Open(); using var count = HubStore.Command(check, "SELECT COUNT(*) FROM subscriptions");
        Assert.Equal(200L, count.ExecuteScalar());
    }
    [Fact]
    public void SchemaUpgradeRetainsNativePreviewBindingsAndRejectsFutureSchema()
    {
        var code = store.BeginLink("42"); store.Approve(code, owner.Id, "42", "ciphertext", "device");
        using (var db = store.Open())
        using (var old = HubStore.Command(db, "PRAGMA user_version=0")) old.ExecuteNonQuery();
        Assert.Equal(owner.Id, new HubStore(directory).LinkedUser("42"));
        Assert.Equal(HubStore.SchemaVersion, ReadSchema());
        using (var db = store.Open())
        using (var future = HubStore.Command(db, "PRAGMA user_version=999")) future.ExecuteNonQuery();
        Assert.Throws<HubError>(() => new HubStore(directory));
        Assert.Throws<HubError>(() => store.Open());
        using var retained = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "hub.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        retained.Open();
        using var version = HubStore.Command(retained, "PRAGMA user_version"); Assert.Equal(999L, version.ExecuteScalar());
        using var binding = HubStore.Command(retained, "SELECT user FROM links WHERE discord='42'");
        Assert.Equal(owner.Id.ToString("N"), binding.ExecuteScalar());
    }
    private long ReadSchema()
    {
        using var db = store.Open(); using var command = HubStore.Command(db, "PRAGMA user_version");
        return (long)command.ExecuteScalar()!;
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
}
