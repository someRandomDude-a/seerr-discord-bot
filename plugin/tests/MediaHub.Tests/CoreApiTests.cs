using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Jellyfin.Plugin.WatchlistDeletion.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Contracts = Jellyfin.Plugin.WatchlistDeletion.Api.Contracts;

namespace MediaHub.Tests;

public sealed class CoreApiTests
{
    private static ServiceProvider Services(FeatureFixture f)
    {
        var media = new Mock<IViewerMedia>();
        var personal = new PersonalService(f.Store, media.Object, f.Settings);
        return new ServiceCollection().AddSingleton(f.Store).AddSingleton(f.Settings).AddSingleton(f.Identity)
            .AddSingleton<IViewerMedia>(media.Object).AddSingleton(personal).AddSingleton<CoreApiProvider>()
            .AddKeyedSingleton<Func<string, string, string, CancellationToken, Task<string>>>(CoreApi.ServiceKey,
                (provider, _) => provider.GetRequiredService<CoreApiProvider>().Invoke)
            .AddWatchlistDeletionClient().BuildServiceProvider();
    }
    [Fact]
    public async Task AbsentCoreFailsClosedWithoutCallingAnyHostService()
    {
        using var services = new ServiceCollection().AddWatchlistDeletionClient().BuildServiceProvider();
        var client = services.GetRequiredService<IWatchlistDeletionClient>(); Assert.False(client.IsAvailable);
        Assert.Equal("core_unavailable", (await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", Guid.NewGuid(), "personal.state"))).Code);
    }
    [Fact]
    public async Task LinkedIdentityIsResolvedLiveAndContractsRoundTripWithoutNativeTokens()
    {
        using var f = new FeatureFixture(); using var services = Services(f); var client = services.GetRequiredService<IWatchlistDeletionClient>();
        var actor = await client.InvokeAsync<Contracts.LinkedActor>("42", null, "identity.resolve"); Assert.Equal(f.User.Id, actor.User);
        var state = await client.InvokeAsync<Contracts.PersonalState>("42", actor.User, "personal.state");
        Assert.Equal(14, state.DeletionRules.RecentActivityDays); Assert.Equal(2, state.EffectiveDeletionDelayDays);
        Assert.Empty(state.Watchlist); Assert.DoesNotContain("token", JsonSerializer.Serialize(actor), StringComparison.OrdinalIgnoreCase);
        f.User.SetPermission(PermissionKind.IsDisabled, true);
        await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<Contracts.PersonalState>("42", actor.User, "personal.state"));
    }
    [Fact]
    public async Task ExpectedUserIsARelinkCheckNeverCallerSelectedAuthority()
    {
        using var f = new FeatureFixture(); using var services = Services(f); var client = services.GetRequiredService<IWatchlistDeletionClient>();
        await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", Guid.NewGuid(), "personal.state"));
        await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", null, "personal.state"));
        await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("43", f.User.Id, "personal.state"));
    }
    [Fact]
    public async Task RevokedNativeTokenImmediatelyBlocksIntegrationReads()
    {
        using var f = new FeatureFixture(); using var services = Services(f); var client = services.GetRequiredService<IWatchlistDeletionClient>();
        await client.InvokeAsync<Contracts.PersonalState>("42", f.User.Id, "personal.state");
        f.Sessions.Setup(s => s.GetSessionByAuthenticationToken("token", "device", It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("private failure"));
        var error = await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", f.User.Id, "personal.state"));
        Assert.DoesNotContain("private failure", error.Message);
    }
    [Fact]
    public async Task StrictEnvelopeRejectsAmbiguousUnknownAndOversizedInput()
    {
        using var f = new FeatureFixture(); using var services = Services(f); var api = services.GetRequiredService<CoreApiProvider>();
        foreach (var input in new[] { "{\"Payload\":{},\"Payload\":{}}", "{\"Payload\":{},\"Admin\":true}", new string('x', 65537) })
        {
            var reply = JsonSerializer.Deserialize<CoreReply>(await api.Invoke("42", "personal.state", input, default))!;
            Assert.False(reply.Success);
        }
    }
    [Fact]
    public async Task OperatorOrAdminOperationsCannotBeInferredFromGenericDispatch()
    {
        using var f = new FeatureFixture(); using var services = Services(f); var client = services.GetRequiredService<IWatchlistDeletionClient>();
        foreach (var op in new[] { "deletions.execute", "deletions.audit", "settings.update", "sql", "unknown.operation" })
            await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", f.User.Id, op));
    }
    [Fact]
    public async Task CancelledUncooperativeWritesHoldTheirSlotAndAreNeverRetried()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string, string, string, CancellationToken, Task<string>>>(CoreApi.ServiceKey,
            (_, _) => (_, _, _, _) => { if (Interlocked.Increment(ref calls) == 8) started.TrySetResult(); return release.Task; }).AddWatchlistDeletionClient().BuildServiceProvider();
        var client = services.GetRequiredService<IWatchlistDeletionClient>();
        var entered = new List<Task<object>>(); var budgets = new List<CancellationTokenSource>();
        try
        {
            for (var i = 0; i < 8; i++) { var budget = new CancellationTokenSource(); budgets.Add(budget); entered.Add(client.InvokeAsync<object>("42", Guid.NewGuid(), "write", cancellation: budget.Token)); }
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); // Test actual uncooperative in-flight writes, not work cancelled before dispatch.
            foreach (var budget in budgets) budget.Cancel();
            foreach (var call in entered) Assert.Equal("core_uncertain", (await Assert.ThrowsAsync<CoreApiException>(() => call)).Code);
            Assert.Equal("core_busy", (await Assert.ThrowsAsync<CoreApiException>(() => client.InvokeAsync<object>("42", Guid.NewGuid(), "write"))).Code);
        }
        finally
        {
            release.TrySetResult(JsonSerializer.Serialize(new CoreReply(true, JsonSerializer.SerializeToElement(new { }))));
            foreach (var budget in budgets) budget.Dispose();
        }
        Assert.Equal(8, calls); // No ninth invocation or automatic retry reached the provider.
    }
    [Fact]
    public async Task SeparatelyLoadedSdkUsesOnlyBclDelegateIdentityAtThePluginBoundary()
    {
        var context = new AssemblyLoadContext("mediahub-sdk-test", isCollectible: true);
        try
        {
            var sdk = context.LoadFromAssemblyPath(typeof(CoreApi).Assembly.Location);
            Assert.NotSame(typeof(CoreApi).Assembly, sdk);
            var registrations = new ServiceCollection();
            registrations.AddKeyedSingleton<Func<string, string, string, CancellationToken, Task<string>>>(CoreApi.ServiceKey,
                (_, _) => (subject, op, json, _) => Task.FromResult(JsonSerializer.Serialize(new CoreReply(true, JsonSerializer.SerializeToElement(subject + ":" + op)))));
            sdk.GetType(typeof(CoreApi).FullName!)!.GetMethod("AddWatchlistDeletionClient")!.Invoke(null, [registrations]);
            using var services = registrations.BuildServiceProvider(); var contract = sdk.GetType(typeof(IWatchlistDeletionClient).FullName!)!;
            var client = services.GetRequiredService(contract);
            var invoke = contract.GetMethod("InvokeAsync")!.MakeGenericMethod(typeof(string));
            Assert.Equal("42:test", await (Task<string>)invoke.Invoke(client, ["42", null, "test", null, CancellationToken.None])!);
        }
        finally { context.Unload(); }
    }
    [Fact]
    public async Task OfflineNativeProposalMaintenanceDoesNotRequireDiscordLinks()
    {
        using var f = new FeatureFixture(); var item = Guid.NewGuid(); var media = new Mock<IViewerMedia>(); var activity = new Mock<IRecentActivity>(); var authority = new Mock<IRequestAuthority>();
        media.Setup(m => m.Find(f.User, item)).Returns(new VisibleItem(item, "Native movie", "Movie")); activity.Setup(a => a.Read(item)).Returns([]);
        authority.Setup(a => a.Verify(f.User, 1, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "verified-pin"));
        var queue = new DeletionQueue(f.Store, f.Settings, new(f.Store, media.Object, f.Settings), media.Object, activity.Object, authority.Object);
        await queue.Propose(f.User, 1, item, default);
        using (var db = f.Store.Open()) using (var removeLink = HubStore.Command(db, "DELETE FROM links")) removeLink.ExecuteNonQuery();
        using var services = new ServiceCollection().AddSingleton(queue).AddSingleton(f.Identity).BuildServiceProvider();
        var maintenance = new DeletionMaintenance(services, NullLogger<DeletionMaintenance>.Instance); await maintenance.Review(default);
        Assert.Equal("queued", Assert.Single(queue.List(f.User)).State); Assert.False(f.Store.IsLinked(f.User.Id));
        f.User.SetPermission(PermissionKind.IsDisabled, true); await maintenance.Review(default);
        Assert.Equal("blocked", Assert.Single(queue.List(f.User)).State);
    }
}
