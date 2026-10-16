using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHub;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class MessagingTests
{
    private static MessageTarget Channel(string id = "123") => new("channel", id, "555");
    private static Messaging Service(FeatureFixture f, FakeDiscord transport) => new(f.Store, f.Settings, f.Operators, transport);
    [Fact]
    public async Task NativeAdministrationDoesNotGrantOperatorAccessAndRevocationIsLive()
    {
        using var f = new FeatureFixture(); f.User.SetPermission(PermissionKind.IsAdministrator, true); f.Update(f.Features(operators: [])); var transport = new FakeDiscord(); var messages = Service(f, transport);
        await Assert.ThrowsAsync<HubError>(() => messages.Destinations(f.Actor, CancellationToken.None)); Assert.Equal(0, transport.Verifications);
        f.Update(f.Features(operators: [], seerr: true)); f.Permissions = 32;
        await Assert.ThrowsAsync<HubError>(() => f.Operators.Require(f.User.Id, CancellationToken.None));
        f.Permissions = 2; Assert.Equal(f.Actor, await f.Operators.Require(f.User.Id, CancellationToken.None));
        f.Permissions = 0; await Assert.ThrowsAsync<HubError>(() => messages.History(f.Actor, 1, CancellationToken.None));
    }
    [Fact]
    public async Task AnnouncementPreviewIsImmutableOwnerBoundAndDoesNotSendUntilConfirmed()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var preview = await messages.Prepare(f.Actor, "announcement", "Hello", [Channel()], [], null, false, CancellationToken.None);
        Assert.Equal(0, transport.Sends); Assert.Equal("prepared", preview.State);
        await Assert.ThrowsAsync<HubError>(() => messages.Confirm(f.Actor with { Discord = "99" }, preview.Id, CancellationToken.None));
        var receipt = await messages.Confirm(f.Actor, preview.Id, CancellationToken.None); Assert.Equal("pending", Assert.Single(receipt.Deliveries).State); Assert.Equal(0, transport.Sends);
        await messages.Confirm(f.Actor, preview.Id, CancellationToken.None); await messages.Deliver(CancellationToken.None);
        Assert.Equal(1, transport.Sends); Assert.Equal("Hello", Assert.Single(transport.SentBodies));
        Assert.Equal("sent", Assert.Single((await messages.Receipt(f.Actor, preview.Id, CancellationToken.None)).Deliveries).State);
        f.Data.VerifyNoOtherCalls(); f.Library.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task NormalChatNonceIsIdempotentAndCannotBeReusedForDifferentContent()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport); const string nonce = "unique-normal-send-001";
        var first = await messages.Prepare(f.Actor, "normal", "First", [Channel()], [], nonce, false, CancellationToken.None);
        await messages.Confirm(f.Actor, first.Id, CancellationToken.None); await messages.Deliver(CancellationToken.None);
        var again = await Service(f, transport).Prepare(f.Actor, "normal", "First", [Channel()], [], nonce, false, CancellationToken.None);
        Assert.Equal(first.Id, again.Id); await messages.Confirm(f.Actor, again.Id, CancellationToken.None); await messages.Deliver(CancellationToken.None); Assert.Equal(1, transport.Sends);
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "normal", "Different", [Channel()], [], nonce, false, CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "normal", "Broadcast", [Channel(), Channel("124")], [], "normal-broadcast-001", false, CancellationToken.None));
    }
    [Fact]
    public async Task AmbiguousSendAndRestartNeverRepeatTheOutgoingMessage()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord { LoseResponse = true }; var messages = Service(f, transport);
        var plan = await messages.Prepare(f.Actor, "announcement", "Check", [Channel()], [], null, false, CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None);
        await messages.Deliver(CancellationToken.None); Assert.Equal("uncertain", Assert.Single((await messages.Receipt(f.Actor, plan.Id, CancellationToken.None)).Deliveries).State);
        await Service(f, transport).Deliver(CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None); Assert.Equal(1, transport.Sends);
    }
    [Fact]
    public async Task LostAuthorizationOrChannelPermissionsBlockPendingJobsBeforeSend()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var plan = await messages.Prepare(f.Actor, "announcement", "Check", [Channel()], [], null, false, CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None);
        transport.Denied = true; await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
        transport.Denied = false; Assert.Equal("blocked", Assert.Single((await messages.Receipt(f.Actor, plan.Id, CancellationToken.None)).Deliveries).State);
        await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
    }
    [Fact]
    public async Task AtomicDeliveryClaimPreventsTwoServiceInstancesSendingTwice()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var plan = await messages.Prepare(f.Actor, "announcement", "Once", [Channel()], [], null, false, CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None);
        await Task.WhenAll(messages.Deliver(CancellationToken.None), Service(f, transport).Deliver(CancellationToken.None)); Assert.Equal(1, transport.Sends);
    }
    [Fact]
    public async Task AllMemberAnnouncementsRequireOptInRespectCapacityAndDeduplicatePeopleAcrossServers()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "announcement", "Members", [Channel()], [], null, true, CancellationToken.None));
        f.Update(f.Features(members: true));
        var plan = await messages.Prepare(f.Actor, "announcement", "Members", [Channel(), new("channel", "124", "556")], [], null, true, CancellationToken.None);
        Assert.Equal(3, plan.Destinations.Count); Assert.Single(plan.Destinations, d => d.Target.Kind == "user");
        f.Update(f.Features(members: true, recipients: 1));
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "announcement", "Members", [Channel()], [], null, true, CancellationToken.None)); Assert.Equal(0, transport.Sends);
    }
    [Fact]
    public async Task UploadsAreScopedBoundedSanitizedAndCannotForwardConfiguredSecrets()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var file = await messages.Upload(f.Actor, "../../bad\r\nname.svg", Encoding.UTF8.GetBytes("<svg onload=alert(1) />"), CancellationToken.None);
        Assert.Equal("badname.svg", file.Filename); Assert.Equal("application/octet-stream", file.Mime);
        await Assert.ThrowsAsync<HubError>(() => messages.Upload(f.Actor, "empty", [], CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => messages.Upload(f.Actor, "huge", new byte[Messaging.FileLimit + 1], CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => messages.DownloadUpload(f.Actor with { User = Guid.NewGuid() }, file.Id, CancellationToken.None));
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "announcement", "bot-private-token", [Channel()], [], null, false, CancellationToken.None));
        var secret = await messages.Upload(f.Actor, "secret.txt", Encoding.UTF8.GetBytes("do not send seerr-private-key"), CancellationToken.None);
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "announcement", "Secret file", [Channel()], [secret.Id], null, false, CancellationToken.None));
        var plan = await messages.Prepare(f.Actor, "announcement", "File", [Channel()], [file.Id], null, false, CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None);
        await Assert.ThrowsAsync<HubError>(() => messages.Prepare(f.Actor, "announcement", "Cannot reuse file", [Channel()], [file.Id], null, false, CancellationToken.None)); Assert.Equal(0, transport.Sends);
    }
    [Fact]
    public async Task InboxProjectsPrivateAssetsAndEnforcesLiveChannelAccessEditAndDelete()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var url = "https://cdn.discordapp.com/attachments/123/456/file.png?hm=private";
        var rich = new JsonObject { ["attachments"] = new JsonArray(new JsonObject { ["key"] = "attachment-0", ["filename"] = "file.png" }), ["assets"] = new JsonObject { ["attachment-0"] = new JsonObject { ["url"] = url, ["filename"] = "file.png" } } };
        var incoming = new MessageView("100", "99", "<script>sender</script>", "555", "Private server", "123", "private", "Original", new JsonObject(), DateTimeOffset.UtcNow, null, false, "in", null, null);
        messages.Receive(incoming, rich, false, true, false);
        var page = await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None); Assert.Single(page.Messages);
        var json = JsonSerializer.Serialize(page); Assert.DoesNotContain("cdn.discordapp.com", json); Assert.DoesNotContain("hm=private", json);
        var asset = await messages.Asset(f.Actor, "100", "attachment-0", CancellationToken.None); Assert.Equal(url, asset.Url);
        transport.Denied = true; Assert.Empty((await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None)).Messages);
        await Assert.ThrowsAsync<HubError>(() => messages.Asset(f.Actor, "100", "attachment-0", CancellationToken.None)); transport.Denied = false;
        messages.Edit("100", "123", "Edited", rich); Assert.Equal("Edited", Assert.Single((await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None)).Messages).Content);
        messages.Delete("100", "123"); var deleted = Assert.Single((await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None)).Messages); Assert.True(deleted.Deleted); Assert.Equal("", deleted.Content);
        await Assert.ThrowsAsync<HubError>(() => messages.Asset(f.Actor, "100", "attachment-0", CancellationToken.None));
    }
    [Fact]
    public async Task CancelExpiryAndRetentionNeverSendOrLoseUncertainTombstones()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var plan = await messages.Prepare(f.Actor, "announcement", "Retained secret content", [Channel()], [], "retained-send-id-001", false, CancellationToken.None);
        await messages.Cancel(f.Actor, plan.Id, CancellationToken.None); await messages.Confirm(f.Actor, plan.Id, CancellationToken.None); Assert.Equal(0, transport.Sends);
        using (var db = f.Store.Open()) using (var old = HubStore.Command(db, "UPDATE message_plans SET created=$0", DateTimeOffset.UtcNow.AddDays(-10).ToString("O"))) old.ExecuteNonQuery();
        await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None);
        var receipt = await messages.Receipt(f.Actor, plan.Id, CancellationToken.None); Assert.Equal("", receipt.Content);
        var repeat = await messages.Prepare(f.Actor, "announcement", "Retained secret content", [Channel()], [], "retained-send-id-001", false, CancellationToken.None); Assert.Equal("retained", repeat.State); Assert.Equal(plan.Id, repeat.Id);
        await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
    }
    [Theory]
    [InlineData("content")] [InlineData("destination")] [InlineData("expiry")]
    public async Task ChangedRetainedPreviewCannotAuthorizeAChatOrBroadcast(string field)
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var preview = await messages.Prepare(f.Actor, "announcement", "Authorized", [Channel()], [], null, false, CancellationToken.None);
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, field switch {
            "content" => "UPDATE message_plans SET content='tampered' WHERE id=$0",
            "destination" => "UPDATE message_plans SET destinations='[]' WHERE id=$0",
            _ => "UPDATE message_plans SET expires='2099-01-01T00:00:00.0000000+00:00' WHERE id=$0" }, preview.Id)) change.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => messages.Confirm(f.Actor, preview.Id, CancellationToken.None)); Assert.Equal(0, transport.Sends);
    }
    [Fact]
    public async Task TamperingWithAConfirmedAttachmentBlocksDeliveryBeforeSend()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var upload = await messages.Upload(f.Actor, "original.txt", Encoding.UTF8.GetBytes("original"), CancellationToken.None);
        var preview = await messages.Prepare(f.Actor, "announcement", "Authorized", [Channel()], [upload.Id], null, false, CancellationToken.None);
        await messages.Confirm(f.Actor, preview.Id, CancellationToken.None);
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, "UPDATE message_files SET body=$0 WHERE id=$1", Encoding.UTF8.GetBytes("tampered"), upload.Id)) change.ExecuteNonQuery();
        await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
    }
    [Fact]
    public async Task ForgingASentDeliveryBackToPendingCannotReplayTheMessage()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var preview = await messages.Prepare(f.Actor, "normal", "Once", [Channel()], [], "never-replay-send-001", false, CancellationToken.None);
        await messages.Confirm(f.Actor, preview.Id, CancellationToken.None); await messages.Deliver(CancellationToken.None); Assert.Equal(1, transport.Sends);
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, "UPDATE message_deliveries SET state='pending' WHERE plan=$0", preview.Id)) change.ExecuteNonQuery();
        await messages.Deliver(CancellationToken.None); Assert.Equal(1, transport.Sends);
    }
    [Fact]
    public async Task IntegrationRevisionChangeBlocksAnAlreadyConfirmedMessage()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var preview = await messages.Prepare(f.Actor, "announcement", "Stale", [Channel()], [], null, false, CancellationToken.None);
        await messages.Confirm(f.Actor, preview.Id, CancellationToken.None); f.Update(f.Features());
        await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
        Assert.Equal("blocked", Assert.Single((await messages.Receipt(f.Actor, preview.Id, CancellationToken.None)).Deliveries).State);
    }
    [Fact]
    public async Task RetentionErasesAttachmentNamesAndDigestsButKeepsTheNormalSendNonce()
    {
        using var f = new FeatureFixture(); var transport = new FakeDiscord(); var messages = Service(f, transport);
        var file = await messages.Upload(f.Actor, "private-filename.txt", Encoding.UTF8.GetBytes("private bytes"), CancellationToken.None);
        var preview = await messages.Prepare(f.Actor, "normal", "Private", [Channel()], [file.Id], "retained-file-send-001", false, CancellationToken.None);
        await messages.Cancel(f.Actor, preview.Id, CancellationToken.None);
        using (var db = f.Store.Open()) using (var old = HubStore.Command(db, "UPDATE message_plans SET created=$0", DateTimeOffset.UtcNow.AddDays(-10).ToString("O"))) old.ExecuteNonQuery();
        await messages.Inbox(f.Actor, "all", null, null, null, "", 1, false, CancellationToken.None);
        using (var db = f.Store.Open()) using (var read = HubStore.Command(db, "SELECT fingerprint FROM message_plans WHERE id=$0", preview.Id)) Assert.DoesNotContain(file.Filename, (string)read.ExecuteScalar()!);
        Assert.Equal("retained", (await messages.Prepare(f.Actor, "normal", "Private", [Channel()], [file.Id], "retained-file-send-001", false, CancellationToken.None)).State);
        await messages.Deliver(CancellationToken.None); Assert.Equal(0, transport.Sends);
    }
    [Theory]
    [InlineData("http://cdn.discordapp.com/attachments/a/b")]
    [InlineData("https://cdn.discordapp.com.evil.test/attachments/a/b")]
    [InlineData("https://user:pass@cdn.discordapp.com/attachments/a/b")]
    [InlineData("https://127.0.0.1/attachments/a/b")]
    [InlineData("file:///server/private")]
    public void AssetProxyRejectsArbitraryTargets(string url) => Assert.False(MessageAssets.Allowed(url));
    [Fact]
    public async Task AssetDownloadIsBoundedAndDoesNotForwardAuthorizationOrFollowRedirects()
    {
        var handler = new AssetHandler(); using var assets = new MessageAssets(handler);
        var file = await assets.Download("https://cdn.discordapp.com/attachments/a/b/file.txt", "../file.txt", CancellationToken.None); Assert.Equal("file.txt", file.Filename); Assert.Equal("application/octet-stream", file.Mime);
        handler.Redirect = true; await Assert.ThrowsAsync<HttpRequestException>(() => assets.Download("https://cdn.discordapp.com/attachments/a/b/file.txt", "file", CancellationToken.None)); Assert.Equal(2, handler.Count);
        handler.Redirect = false; handler.Huge = true; await Assert.ThrowsAsync<HubError>(() => assets.Download("https://cdn.discordapp.com/attachments/a/b/file.txt", "file", CancellationToken.None));
    }
    private sealed class AssetHandler : HttpMessageHandler
    {
        public bool Redirect, Huge; public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++; Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-Emby-Token"));
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            if (Redirect) response.Headers.Location = new("http://127.0.0.1/private"); if (Huge) response.Content.Headers.ContentLength = Messaging.FileLimit + 1; return Task.FromResult(response);
        }
    }
    private sealed class FakeDiscord : IDiscordMessaging
    {
        public bool Denied, LoseResponse; public int Sends, Verifications; public readonly List<string> SentBodies = [];
        public Task<IReadOnlyList<MessageDestination>> Destinations(OperatorActor actor, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<MessageDestination>>([new(Channel(), "Server / channel")]);
        public Task<IReadOnlyList<MessageTarget>> Members(OperatorActor actor, string guild, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<MessageTarget>>([new("user", "99", guild)]);
        public Task<MessageDestination> Verify(OperatorActor actor, MessageTarget target, bool writing, bool files, CancellationToken cancellation)
        { Verifications++; if (Denied) throw new HubError("Permissions revoked"); return Task.FromResult(new MessageDestination(target, "Allowed " + target.Id)); }
        public Task<string> Send(MessageTarget target, string content, IReadOnlyList<MessageBlob> files, CancellationToken cancellation)
        { Interlocked.Increment(ref Sends); lock (SentBodies) SentBodies.Add(content); if (LoseResponse) throw new HttpRequestException("Applied but response lost"); return Task.FromResult((1000000 + Sends).ToString()); }
    }
}
