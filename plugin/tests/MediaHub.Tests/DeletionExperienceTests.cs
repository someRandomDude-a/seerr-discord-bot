using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MediaHub;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class DeletionExperienceTests
{
    [Fact]
    public void PersonalMessageIsOptionalAndPreservesLegacySerialization()
    {
        Assert.DoesNotContain("DeletionMessage", JsonSerializer.Serialize(new PersonalPreferences()));
        var legacy = JsonSerializer.Deserialize<PersonalPreferences>("{\"JellyfinNotifications\":true}")!;
        Assert.Null(legacy.DeletionMessage);
        Assert.Equal("Allow deletion?", DeletionMessages.Render(null, "Movie", 2, 14));
        Assert.Equal("Movie {delayDays} in 2 days / 14", DeletionMessages.Render("{title} in {delayDays} days / {activityDays}", "Movie {delayDays}", 2, 14));
    }
    [Theory]
    [InlineData("{requester}")] [InlineData("{user}")] [InlineData("test\nmessage")] [InlineData("{{title}}")]
    public void PersonalMessageCannotReferenceOtherPeopleOrInjectControlCharacters(string value) => Assert.Throws<HubError>(() => DeletionMessages.Validate(value));
    [Fact]
    public async Task StatusAndNoticesArePersonalAndPassiveAndNewActivityInvalidatesOldConsent()
    {
        using var f = new FeatureFixture(); var item = Guid.NewGuid(); var other = new User("Other private viewer", "provider", "reset") { Id = Guid.NewGuid() };
        var media = new Mock<IViewerMedia>(); media.Setup(m => m.Find(It.IsAny<User>(), item)).Returns(new VisibleItem(item, "Movie", "Movie"));
        var at = DateTimeOffset.UtcNow.AddHours(-1); var activity = new Mock<IRecentActivity>();
        activity.Setup(a => a.Read(item)).Returns(() => new[] { new RecentViewer(f.User.Id, at), new RecentViewer(other.Id, at) });
        var authority = new Mock<IRequestAuthority>(); authority.Setup(a => a.Verify(f.User, 1, item, It.IsAny<CancellationToken>())).ReturnsAsync(new VerifiedDeletionTarget(item, "pin"));
        var personal = new PersonalService(f.Store, media.Object, f.Settings); var queue = new DeletionQueue(f.Store, f.Settings, personal, media.Object, activity.Object, authority.Object);
        await queue.Propose(f.User, 1, item, default); var proposal = Assert.Single(queue.List(f.User));
        var before = personal.State(other); personal.Save(other, before.Revision, before.Preferences with { DeletionMessage = "Review {title}, delay {delayDays} days" });
        var notice = Assert.Single(queue.Notices(other)); Assert.Equal("Review Movie, delay 2 days", notice.Message);
        Assert.Equal("Allow deletion?", DeletionMessages.Render(personal.State(f.User).Preferences.DeletionMessage, "Movie", 2, 14));
        var status = queue.Status(other, proposal.Id); Assert.Equal("consent_required", status.RequiredAction); Assert.False(status.CanUndo); Assert.False(status.Yours);
        Assert.Equal(at, status.YourLastActivity); Assert.Equal(notice.Id, status.Notice);
        var json = JsonSerializer.Serialize(status); Assert.DoesNotContain(other.Id.ToString(), json); Assert.DoesNotContain(f.User.Id.ToString(), json); Assert.DoesNotContain("Other private viewer", json);
        await queue.Respond(other, notice.Id, true, default); Assert.Equal("yes", queue.Status(other, proposal.Id).YourConsent);
        at = DateTimeOffset.UtcNow; Assert.Equal("required", queue.Status(other, proposal.Id).YourConsent);
        Assert.Null(queue.Status(other, proposal.Id).Notice); // Only a fresh maintenance recheck can issue the new activity-bound notice.
        Assert.Equal(proposal.Item, queue.Status(f.User, proposal.Id).Item); f.Data.VerifyNoOtherCalls();
        media.Setup(m => m.Find(other, item)).Returns((VisibleItem?)null);
        Assert.Throws<HubError>(() => queue.Status(other, proposal.Id));
    }
}
