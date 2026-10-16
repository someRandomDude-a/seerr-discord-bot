using Jellyfin.Plugin.MediaHub;
using Xunit;

namespace MediaHub.Tests;

public sealed class DiscordControlTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mediahub-controls-" + Guid.NewGuid());
    [Fact]
    public void ControlsRequireExactActorNativeIdentityAndMessageAndInvalidateOldViewTogether()
    {
        var store = new HubStore(directory); var controls = new DiscordControls(store, new(store)); var user = Guid.NewGuid(); var view = HubStore.Nonce();
        var first = controls.Add(view, "42", user, new("follow", Revision: 7))["mediahub:control:".Length..];
        var second = controls.Add(view, "42", user, new("details"))["mediahub:control:".Length..];
        Assert.Throws<HubError>(() => controls.Claim(first, "42", user, 100));
        controls.Bind(view, 100);
        Assert.Throws<HubError>(() => controls.Claim(first, "43", user, 100));
        Assert.Throws<HubError>(() => controls.Claim(first, "42", Guid.NewGuid(), 100));
        Assert.Throws<HubError>(() => controls.Claim(first, "42", user, 101));
        var reopened = new HubStore(directory); var restored = new DiscordControls(reopened, new(reopened)); Assert.Equal(7, restored.Claim(first, "42", user, 100).Revision);
        Assert.Throws<HubError>(() => restored.Claim(first, "42", user, 100));
        Assert.Throws<HubError>(() => restored.Claim(second, "42", user, 100));
    }
    [Fact]
    public void ExpiredControlsCannotPerformAction()
    {
        var store = new HubStore(directory); var controls = new DiscordControls(store, new(store)); var user = Guid.NewGuid();
        var id = controls.Add("view", "42", user, new("confirm", Token: "request"))["mediahub:control:".Length..]; controls.Bind("view", 100);
        using (var db = store.Open()) using (var expire = HubStore.Command(db, "UPDATE discord_controls SET expires=$0", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"))) expire.ExecuteNonQuery();
        Assert.Throws<HubError>(() => controls.Claim(id, "42", user, 100));
    }
    [Theory]
    [InlineData("payload")] [InlineData("expires")] [InlineData("message")] [InlineData("replay")]
    public void AlteredControlsCannotAuthorizeAnUnselectedAction(string field)
    {
        var store = new HubStore(directory); var controls = new DiscordControls(store, new(store)); var user = Guid.NewGuid();
        var id = controls.Add("view", "42", user, new("details"))["mediahub:control:".Length..]; controls.Bind("view", 100);
        if (field == "replay") controls.Claim(id, "42", user, 100);
        using (var db = store.Open()) using (var change = HubStore.Command(db, field switch {
            "payload" => "UPDATE discord_controls SET payload='{\"Action\":\"confirm\",\"Token\":\"other-request\"}'",
            "expires" => "UPDATE discord_controls SET expires='2099-01-01T00:00:00.0000000+00:00'",
            "message" => "UPDATE discord_controls SET message='101'",
            _ => "UPDATE discord_controls SET state='active'" })) change.ExecuteNonQuery();
        Assert.Throws<HubError>(() => controls.Claim(id, "42", user, field == "message" ? 101UL : 100UL));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
