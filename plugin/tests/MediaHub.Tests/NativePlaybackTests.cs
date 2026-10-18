using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class NativePlaybackTests
{
    private static async Task<(SessionInfo Actor, SessionInfo Target, Guid Item)> Setup(FeatureFixture f)
    {
        f.User.SetPermission(PermissionKind.EnableMediaPlayback, true); var actor = await f.Identity.DiscordSession("42");
        var target = new SessionInfo(f.Sessions.Object, NullLogger.Instance) { Id = "actual-tv", DeviceId = "tv-device", UserId = f.User.Id, DeviceName = "Owned TV", Client = "Jellyfin TV", Capabilities = new ClientCapabilities { SupportsMediaControl = true, PlayableMediaTypes = [MediaType.Video] } };
        var controller = new Mock<ISessionController>(); controller.SetupGet(c => c.IsSessionActive).Returns(true); controller.SetupGet(c => c.SupportsMediaControl).Returns(true); target.AddController(controller.Object);
        f.Sessions.SetupGet(s => s.Sessions).Returns([actor, target]);
        var movie = new Mock<Movie>(); movie.Object.Id = Guid.NewGuid(); movie.Object.Name = "Visible movie"; movie.Setup(m => m.IsVisibleStandalone(f.User)).Returns(true); movie.SetupGet(m => m.MediaType).Returns(MediaType.Video);
        f.Library.Setup(l => l.GetItemById(movie.Object.Id)).Returns(movie.Object); return (actor, target, movie.Object.Id);
    }
    private static NativePlayback Service(FeatureFixture f) => new(f.Store, f.Settings, f.Identity, f.Sessions.Object, f.Library.Object);
    [Fact]
    public async Task NativeDevicePlaybackCannotBypassCompanionPartyPlaybackAuthority()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var provider = new Mock<Jellyfin.Plugin.PrivateSyncPlay.Api.IWatchPartyService>(); provider.SetupGet(p => p.IsAvailable).Returns(true);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Plugin.PrivateSyncPlay.Api.WatchPartyInfo(Guid.NewGuid(), f.User.Id, Jellyfin.Plugin.PrivateSyncPlay.Api.WatchPartyVisibility.Private, 4, 1, null));
        var service = new NativePlayback(f.Store, f.Settings, f.Identity, f.Sessions.Object, f.Library.Object, provider.Object); var plan = service.Prepare(actor, target.Id, item);
        await Assert.ThrowsAsync<HubError>(() => service.Confirm(actor, plan.Id, () => Task.CompletedTask, default));
        f.Sessions.Verify(s => s.SendPlayCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task RealOwnPlayerNeedsAnExplicitPreviewAndOnceOnlyNativeCommandWithoutPlaybackFabrication()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var service = Service(f); var sends = 0;
        f.Sessions.Setup(s => s.SendPlayCommand(actor.Id, target.Id, It.Is<PlayRequest>(p => p.ItemIds.SequenceEqual(new[] { item }) && p.PlayCommand == PlayCommand.PlayNow && p.ControllingUserId == f.User.Id), It.IsAny<CancellationToken>())).Returns(() => { sends++; return Task.CompletedTask; });
        Assert.Equal(target.Id, Assert.Single(service.Destinations(actor)).Id); var preview = service.Prepare(actor, target.Id, item); Assert.Equal(0, sends);
        Assert.Equal("accepted", (await service.Confirm(actor, preview.Id, () => Task.CompletedTask, default)).State);
        Assert.False(service.Receipt(actor, preview.Id).ObservedPlayingItem); await Service(f).Confirm(actor, preview.Id, () => Task.CompletedTask, default); Assert.Equal(1, sends);
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task AnotherUsersPlayerCannotBeListedOrSelectedEvenByAnAdministrator()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); target.UserId = Guid.NewGuid(); f.User.SetPermission(PermissionKind.IsAdministrator, true);
        var service = Service(f); Assert.Empty(service.Destinations(actor)); Assert.Throws<HubError>(() => service.Prepare(actor, target.Id, item));
        f.Sessions.Verify(s => s.SendPlayCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task DisconnectPermissionLossAndChangedTargetBlockAnUnsentPreview()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var service = Service(f); var preview = service.Prepare(actor, target.Id, item);
        target.UserId = Guid.NewGuid(); await Assert.ThrowsAsync<HubError>(() => service.Confirm(actor, preview.Id, () => Task.CompletedTask, default));
        target.UserId = f.User.Id; f.User.SetPermission(PermissionKind.EnableMediaPlayback, false); await Assert.ThrowsAsync<HubError>(() => service.Confirm(actor, preview.Id, () => Task.CompletedTask, default));
        f.Sessions.Verify(s => s.SendPlayCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task UncertainCommandsCannotBeRepeatedOrReplacedWithoutExplicitReceiptAcknowledgement()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var service = Service(f); var sends = 0;
        f.Sessions.Setup(s => s.SendPlayCommand(actor.Id, target.Id, It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>())).Returns(() => { sends++; throw new IOException("Possibly delivered"); });
        var preview = service.Prepare(actor, target.Id, item); Assert.Equal("uncertain", (await service.Confirm(actor, preview.Id, () => Task.CompletedTask, default)).State);
        await Service(f).Confirm(actor, preview.Id, () => Task.CompletedTask, default); Assert.Equal(1, sends); Assert.Throws<HubError>(() => Service(f).Prepare(actor, target.Id, item));
        Assert.Equal("reviewed", service.Acknowledge(actor, preview.Id).State); _ = service.Prepare(actor, target.Id, item); Assert.Equal(1, sends); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task TamperedStateCannotTurnAnAcceptedCommandIntoANewSend()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var service = Service(f);
        f.Sessions.Setup(s => s.SendPlayCommand(actor.Id, target.Id, It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var preview = service.Prepare(actor, target.Id, item); await service.Confirm(actor, preview.Id, () => Task.CompletedTask, default);
        using (var db = f.Store.Open()) using (var write = HubStore.Command(db, "UPDATE playback_operations SET state='prepared' WHERE id=$0", preview.Id)) write.ExecuteNonQuery();
        await Assert.ThrowsAnyAsync<Exception>(() => Service(f).Confirm(actor, preview.Id, () => Task.CompletedTask, default));
        Assert.ThrowsAny<Exception>(() => Service(f).Prepare(actor, target.Id, item));
        f.Sessions.Verify(s => s.SendPlayCommand(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task UncooperativeNativeCommandKeepsItsBoundedSlotUntilItFinishes()
    {
        using var f = new FeatureFixture(); var (actor, target, item) = await Setup(f); var service = Service(f); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Sessions.Setup(s => s.SendPlayCommand(actor.Id, target.Id, It.IsAny<PlayRequest>(), It.IsAny<CancellationToken>())).Returns(() => { entered.TrySetResult(); return completion.Task; });
        var preview = service.Prepare(actor, target.Id, item); using var cancellation = new CancellationTokenSource();
        var request = service.Confirm(actor, preview.Id, () => Task.CompletedTask, cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.Equal("uncertain", (await request).State); await Assert.ThrowsAsync<HubError>(() => service.Confirm(actor, preview.Id, () => Task.CompletedTask, default));
        Assert.Throws<HubError>(() => service.Acknowledge(actor, preview.Id)); completion.SetResult();
    }
}
