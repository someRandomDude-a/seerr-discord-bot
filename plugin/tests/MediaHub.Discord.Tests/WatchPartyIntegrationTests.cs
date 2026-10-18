extern alias Integration;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Jellyfin.Plugin.PrivateSyncPlay.Api;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using WatchPartyIntegration = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.WatchPartyIntegration;
using WatchPartyFailure = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.WatchPartyFailure;
using WatchPartyController = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.WatchPartyController;
using DiscordPartyCommands = Integration::Jellyfin.Plugin.MediaHub.DiscordIntegration.DiscordPartyCommands;
using DiscordError = Integration::Jellyfin.Plugin.MediaHub.HubError;

namespace MediaHub.Tests;

public sealed class WatchPartyIntegrationTests
{
    private static async Task<SessionInfo> Actor(FeatureFixture f)
    {
        var actor = await f.Identity.DiscordSession("42");
        f.Sessions.SetupGet(s => s.Sessions).Returns([actor]); return actor;
    }
    private static Mock<IWatchPartyService> Provider()
    {
        var provider = new Mock<IWatchPartyService>(MockBehavior.Strict);
        provider.SetupGet(p => p.IsAvailable).Returns(true); return provider;
    }
    private static WatchPartyInfo Party(FeatureFixture f, Guid? owner = null) => new(Guid.NewGuid(), owner ?? f.User.Id, WatchPartyVisibility.Private, 4, 1, "never-return-this-code");
    private static WatchPartyInvite Invite(Guid party, CreateInviteOptions options, string? url = null) => new(Guid.NewGuid(), party,
        new Uri(url ?? options.ServerBaseUrl.AbsoluteUri.TrimEnd('/') + "/PrivateSyncPlay/Watch#secret-token-long-enough"), false, 1, options.ExpiresAt);
    private static WatchPartyIntegration Integration(FeatureFixture f, IWatchPartyService provider) => new(provider, f.Sessions.Object,
        id => f.Users.Object.GetUserById(id) is { } user && !user.HasPermission(PermissionKind.IsDisabled),
        () => f.Settings.Current.PublicJellyfinUrl, value => f.Settings.Authenticate("MediaHub.PartyManagement.v1", value), () => true);
    private static WatchPartyManagement OwnerManagement(Guid party, Guid? account = null, Guid? guest = null) => new(party, new(true, false, true, true, 4), guest is null ? 2 : 3, [],
        [new(Guid.NewGuid(), "Owner", 1, true, new(true, true), new(true, true)), new(account ?? Guid.NewGuid(), "Viewer", 1, false, new(true, false), new(true, false))],
        guest is Guid id ? [new(id, "Anonymous lease", 1)] : [], []);

    [Fact]
    public async Task OwnerManagementExposesNoUserSessionOrInvitationCredentialsAndUsesFreshRevision()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f); var snapshot = OwnerManagement(party.PartyId);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.GetManagementAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var view = await Integration(f, provider.Object).Management(actor, party.PartyId, default);
        Assert.Equal(64, view.Revision.Length); Assert.Equal(2, view.Accounts.Count);
        Assert.DoesNotContain("never-return-this-code", JsonSerializer.Serialize(view)); Assert.DoesNotContain(actor.Id, JsonSerializer.Serialize(view));
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task AdministratorNonOwnerCannotManageOrModerateAnotherParty()
    {
        using var f = new FeatureFixture(); f.User.SetPermission(PermissionKind.IsAdministrator, true); var actor = await Actor(f); var provider = Provider(); var party = Party(f, Guid.NewGuid());
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        var integration = Integration(f, provider.Object);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Management(actor, party.PartyId, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Moderate(actor, party.PartyId, Guid.NewGuid(), "revision", "kick-account", false, default))).Status);
    }
    [Fact]
    public async Task PolicyUpdateCannotEnableGuestsAndStaleSnapshotCannotChangePolicy()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f); var snapshot = OwnerManagement(party.PartyId);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.GetManagementAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CancellationToken>())).ReturnsAsync(() => snapshot);
        provider.Setup(p => p.UpdatePolicyAsync(f.User.Id, actor.Id, party.PartyId, It.Is<WatchPartyPolicy>(p => !p.AllowGuests && !p.AllowJoins && p.MaxViewers == 4), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var integration = Integration(f, provider.Object); var view = await integration.Management(actor, party.PartyId, default);
        Assert.True(await integration.UpdatePolicy(actor, party.PartyId, new(view.Revision, false, true, true, 4), default));
        snapshot = snapshot with { Policy = snapshot.Policy with { AllowJoins = false } };
        Assert.Equal(409, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.UpdatePolicy(actor, party.PartyId, new(view.Revision, true, true, true, 4), default))).Status);
        provider.Verify(p => p.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<WatchPartyPolicy>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task GuestKickRequiresSharedEffectConfirmationAndOwnerCannotBeDemotedOrKicked()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f); var guest = Guid.NewGuid(); var snapshot = OwnerManagement(party.PartyId, guest: guest);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.GetManagementAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        provider.Setup(p => p.KickGuestAndRevokeInviteAsync(f.User.Id, actor.Id, party.PartyId, guest, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var integration = Integration(f, provider.Object); var view = await integration.Management(actor, party.PartyId, default); var owner = snapshot.Accounts.Single(a => a.IsOwner).ParticipantId;
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Moderate(actor, party.PartyId, guest, view.Revision, "revoke-guest-link", false, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Moderate(actor, party.PartyId, owner, view.Revision, "kick-account", false, default))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.AccountPermissions(actor, party.PartyId, owner, view.Revision, false, false, default))).Status);
        Assert.True(await integration.Moderate(actor, party.PartyId, guest, view.Revision, "revoke-guest-link", true, default));
        provider.Verify(p => p.KickGuestAndRevokeInviteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once); f.Data.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingSdkProviderIsContainedAndNeedsNoMainPluginOrPlaybackEngine()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f);
        using var services = new ServiceCollection().AddPrivateSyncPlayClient().BuildServiceProvider();
        var integration = Integration(f, services.GetRequiredService<IWatchPartyService>());
        Assert.False(integration.Status().Available);
        Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Current(actor, default))).Status);
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task MissingCoreBlocksNativeWatchPartyOperationsEvenWhenPrivateSyncPlayIsRegistered()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        var integration = new WatchPartyIntegration(provider.Object, f.Sessions.Object, _ => true, () => "https://jellyfin.example/base", _ => throw new InvalidOperationException("must not sign"), () => false);
        Assert.False(integration.Status().Available);
        Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Current(actor, default))).Status);
        provider.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task CreationUsesExactActiveActorAndAtomicPrivateSignedInOnlyInviteWithTrustedBase()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync((WatchPartyInfo?)null);
        provider.Setup(p => p.CreatePartyWithInviteAsync(f.User.Id, actor.Id,
            It.Is<CreatePartyOptions>(o => o.Name == "Movie night" && o.Visibility == WatchPartyVisibility.Private && o.MaxViewers == null),
            It.Is<CreateInviteOptions>(o => o.ServerBaseUrl.AbsoluteUri == "https://jellyfin.example/base" && !o.AllowGuests && o.MaxUses == 1 && o.ExpiresAt > DateTimeOffset.UtcNow),
            It.IsAny<CancellationToken>())).Returns((Guid _, string _, CreatePartyOptions _, CreateInviteOptions options, CancellationToken _) => Task.FromResult(new WatchPartyWithInvite(party, Invite(party.PartyId, options))));
        var result = await Integration(f, provider.Object).Create(actor, " Movie night ", default);
        Assert.True(result.Party.Owner); Assert.Equal(party.PartyId, result.Party.PartyId);
        Assert.StartsWith("https://jellyfin.example/base/PrivateSyncPlay/Watch#", result.Invite.Url);
        Assert.DoesNotContain("never-return-this-code", JsonSerializer.Serialize(result));
        provider.VerifyAll(); f.Data.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(400)] [InlineData(403)] [InlineData(404)] [InlineData(409)] [InlineData(429)] [InlineData(503)] [InlineData(500)]
    public async Task RegisteredButUnhealthyProviderPreservesSafeStatusWithoutReflectingItsMessage(int status)
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new WatchPartyException(status, "private-bearer-token"));
        var integration = Integration(f, provider.Object); Assert.True(integration.Status().Available);
        var error = await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Current(actor, default));
        Assert.Equal(status == 500 ? 503 : status, error.Status); Assert.DoesNotContain("private-bearer-token", error.Message);
    }
    [Fact]
    public async Task AdministratorParticipantCannotInviteRevokeOrEndAnotherOwnersParty()
    {
        using var f = new FeatureFixture(); f.User.SetPermission(PermissionKind.IsAdministrator, true);
        var actor = await Actor(f); var provider = Provider(); var party = Party(f, Guid.NewGuid());
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        var integration = Integration(f, provider.Object);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Invite(actor, party.PartyId, default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Revoke(actor, party.PartyId, Guid.NewGuid(), default))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.End(actor, party.PartyId, default))).Status);
        var state = await integration.Current(actor, default); Assert.False(state.Owner);
        Assert.DoesNotContain("never-return-this-code", JsonSerializer.Serialize(state));
    }
    [Fact]
    public async Task StalePartyAndDisconnectedSessionNeverReachProviderMutation()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Party(f));
        var integration = Integration(f, provider.Object);
        Assert.Equal(409, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.End(actor, Guid.NewGuid(), default))).Status);
        f.Sessions.SetupGet(s => s.Sessions).Returns(Array.Empty<SessionInfo>());
        Assert.Equal(403, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Create(actor, "no fake host", default))).Status);
    }
    [Theory]
    [InlineData("https://attacker.example/base/PrivateSyncPlay/Watch#secret-token-long-enough")]
    [InlineData("https://jellyfin.example/other/PrivateSyncPlay/Watch#secret-token-long-enough")]
    [InlineData("https://jellyfin.example/base/PrivateSyncPlay/Watch?token=private#secret-token-long-enough")]
    [InlineData("https://jellyfin.example/base/PrivateSyncPlay/Watch")]
    public async Task ProviderInviteCannotChangeTrustedOriginBasePathOrLeakIntoQueries(string url)
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.CreateInviteAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, Guid _, CreateInviteOptions options, CancellationToken _) => Task.FromResult(Invite(party.PartyId, options, url)));
        var error = await Assert.ThrowsAsync<WatchPartyFailure>(() => Integration(f, provider.Object).Invite(actor, party.PartyId, default));
        Assert.Equal(503, error.Status); Assert.DoesNotContain(url, error.Message);
    }
    [Fact]
    public async Task AmbiguousCreationIsNotRetriedAndCurrentPartyPreventsDuplicateCreation()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); WatchPartyInfo? current = null;
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => current);
        provider.Setup(p => p.CreatePartyWithInviteAsync(f.User.Id, actor.Id, It.IsAny<CreatePartyOptions>(), It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() => { current = Party(f); throw new InvalidOperationException("private exception"); });
        var integration = Integration(f, provider.Object);
        Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Create(actor, "Party", default))).Status);
        Assert.True((await integration.Current(actor, default)).Active);
        Assert.Equal(409, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Create(actor, "Party", default))).Status);
        provider.Verify(p => p.CreatePartyWithInviteAsync(f.User.Id, actor.Id, It.IsAny<CreatePartyOptions>(), It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task UncooperativeProviderKeepsBoundedSlotAfterCancellationWithoutBlockingOtherHostWork()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        var pending = new TaskCompletionSource<WatchPartyInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var integration = Integration(f, provider.Object); using var cancellation = new CancellationTokenSource();
        var first = integration.Current(actor, cancellation.Token); cancellation.Cancel();
        Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyFailure>(() => first)).Status);
        Assert.Equal(429, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Current(actor, default))).Status);
        pending.SetResult(null);
    }
    [Fact]
    public async Task NativeControllerRejectsApiKeyAndCannotAcceptActorFromRequestBody()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        var identity = new Integration::Jellyfin.Plugin.MediaHub.HostIdentity(new Integration::Jellyfin.Plugin.MediaHub.Rpc(Mock.Of<Jellyfin.Plugin.WatchlistDeletion.Api.IWatchlistDeletionClient>()), f.Users.Object, f.Sessions.Object);
        using var services = new ServiceCollection().AddSingleton(identity).AddSingleton(Integration(f, provider.Object)).BuildServiceProvider();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-IsApiKey", "True"), new("Jellyfin-UserId", f.User.Id.ToString())], "ApiKey")) };
        var controller = new WatchPartyController(services) { ControllerContext = new() { HttpContext = http } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Create(new("not a host"), default)).StatusCode);
        provider.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task SynchronouslyBlockingProviderDoesNotBlockTheNativeRequestThreadOrDefeatCancellation()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).Returns(() =>
        { entered.SetResult(); release.Wait(); finished.SetResult(); return Task.FromResult<WatchPartyInfo?>(null); });
        var integration = Integration(f, provider.Object); using var cancellation = new CancellationTokenSource();
        var request = integration.Current(actor, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            Assert.Equal(503, (await Assert.ThrowsAsync<WatchPartyFailure>(() => request)).Status);
            Assert.Equal(429, (await Assert.ThrowsAsync<WatchPartyFailure>(() => integration.Current(actor, default))).Status);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
    [Fact]
    public async Task DiscordGeneratesProviderInviteForExactLinkedNativeOwnerWithoutImplementingAdmission()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f);
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.CreateInviteAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, Guid _, CreateInviteOptions options, CancellationToken _) => Task.FromResult(Invite(party.PartyId, options)));
        var commands = new DiscordPartyCommands(Integration(f, provider.Object), f.Identity.DiscordSession);
        var link = await commands.Invite("42", f.User.Id, party.PartyId, default);
        Assert.Equal("https://jellyfin.example/base/PrivateSyncPlay/Watch#secret-token-long-enough", link.Url);
        Assert.DoesNotContain("AccessToken", JsonSerializer.Serialize(link));
        provider.Verify(p => p.CreateInviteAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task DiscordRelinkAfterInviteGenerationWithholdsTheLinkWithoutRollbackOrRetry()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider(); var party = Party(f); var reads = 0;
        provider.Setup(p => p.GetCurrentPartyAsync(f.User.Id, actor.Id, It.IsAny<CancellationToken>())).ReturnsAsync(party);
        provider.Setup(p => p.CreateInviteAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, Guid _, CreateInviteOptions options, CancellationToken _) => Task.FromResult(Invite(party.PartyId, options)));
        var commands = new DiscordPartyCommands(Integration(f, provider.Object), _ =>
        {
            if (++reads > 1) throw new DiscordError("Linked player changed.");
            return Task.FromResult(actor);
        });
        await Assert.ThrowsAsync<DiscordError>(() => commands.Invite("42", f.User.Id, party.PartyId, default));
        provider.Verify(p => p.CreateInviteAsync(f.User.Id, actor.Id, party.PartyId, It.IsAny<CreateInviteOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.RevokeInviteAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task DiscordCallerSelectedUserCannotReachTheProvider()
    {
        using var f = new FeatureFixture(); var actor = await Actor(f); var provider = Provider();
        var commands = new DiscordPartyCommands(Integration(f, provider.Object), f.Identity.DiscordSession);
        await Assert.ThrowsAsync<DiscordError>(() => commands.Create("42", Guid.NewGuid(), "not your player", default));
        provider.VerifyNoOtherCalls();
    }
}
