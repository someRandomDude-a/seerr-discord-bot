using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace MediaHub.Tests;

public sealed class NotificationHookTests
{
    private const string Key = "notification-secret-with-at-least-32-characters";
    private static string Signature(string at, string nonce, byte[] body, string key = Key) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes("MediaHub.NotificationHook.v1\n" + at + "\n" + nonce + "\n").Concat(body).ToArray()));
    [Fact]
    public async Task SignedEventsWakeOnlyOnceAndCannotSupplyAnActorOrMutation()
    {
        var signals = new NotificationSignals(); var now = DateTimeOffset.UtcNow; var at = now.ToUnixTimeSeconds().ToString(); var nonce = new string('a', 32); var body = Encoding.UTF8.GetBytes("{\"user\":\"administrator\",\"delete\":true,\"url\":\"http://attacker\"}");
        Assert.True(signals.Accept(at, nonce, Signature(at, nonce, body), body, Key, now));
        Assert.False(signals.Accept(at, nonce.ToUpperInvariant(), Signature(at, nonce.ToUpperInvariant(), body), body, Key, now));
        Assert.True(await signals.Wait(TimeSpan.Zero, default)); Assert.False(await signals.Wait(TimeSpan.Zero, default));
        var next = new string('b', 32); Assert.True(signals.Accept(at, next, Signature(at, next, body), body, Key, now)); Assert.False(await signals.Wait(TimeSpan.Zero, default));
    }
    [Theory]
    [InlineData("expired")] [InlineData("future")] [InlineData("body")] [InlineData("secret")] [InlineData("oversized")]
    public void ChangedOrExpiredEventsNeverWakeTheWorker(string change)
    {
        var signals = new NotificationSignals(); var now = DateTimeOffset.UtcNow; var at = now.AddSeconds(change == "expired" ? -301 : change == "future" ? 31 : 0).ToUnixTimeSeconds().ToString();
        var nonce = new string('a', 32); var body = Encoding.UTF8.GetBytes("{}"); var signature = Signature(at, nonce, body);
        Assert.False(signals.Accept(at, nonce, signature, change == "body" ? [1] : change == "oversized" ? new byte[65537] : body, change == "secret" ? "another-key" : Key, now));
    }
    [Fact]
    public async Task NativeHookIsDisabledByDefaultRequiresHttpsAndNeverReturnsItsSecret()
    {
        using var f = new FeatureFixture(); var signals = new NotificationSignals();
        var http = new DefaultHttpContext(); http.Request.Scheme = "https";
        var controller = new NotificationHookController(f.Settings, signals) { ControllerContext = new() { HttpContext = http } };
        Assert.IsType<UnauthorizedResult>(await controller.Receive(default));
        f.Update(f.Features() with { NotificationHookKey = Key });
        Assert.DoesNotContain(Key, System.Text.Json.JsonSerializer.Serialize(f.Settings.Public()));
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); var nonce = new string('a', 32); var body = Encoding.UTF8.GetBytes("{}");
        http.Request.Headers["X-MediaHub-Timestamp"] = at; http.Request.Headers["X-MediaHub-Nonce"] = nonce; http.Request.Headers["X-MediaHub-Signature"] = Signature(at, nonce, body); http.Request.Body = new MemoryStream(body);
        Assert.IsType<AcceptedResult>(await controller.Receive(default)); Assert.True(await signals.Wait(TimeSpan.Zero, default));
        http.Request.Scheme = "http"; http.Request.Body = new MemoryStream(body); Assert.IsType<UnauthorizedResult>(await controller.Receive(default));
        f.Data.VerifyNoOtherCalls(); f.Library.VerifyNoOtherCalls();
    }
    [Fact]
    public void OptionalHookExtensionDoesNotChangeExistingSignedSettingsSerialization()
    {
        using var f = new FeatureFixture(); var path = Path.Combine(f.Directory, "settings.json");
        Assert.DoesNotContain("NotificationHook", File.ReadAllText(path));
        Assert.Equal(f.Settings.Current.Revision, new SettingsService(f.Store).Current.Revision);
        f.Update(f.Features() with { NotificationHookKey = Key }); Assert.DoesNotContain(Key, File.ReadAllText(path));
        f.Update(f.Features() with { NotificationHookKey = "" }); Assert.DoesNotContain("NotificationHookKeyEncrypted", File.ReadAllText(path));
        Assert.Equal(f.Settings.Current.Revision, new SettingsService(f.Store).Current.Revision);
    }
}
