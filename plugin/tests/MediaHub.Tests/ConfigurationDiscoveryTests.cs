using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaHub.Tests;

public sealed class ConfigurationDiscoveryTests
{
    private sealed class Handler(Func<HttpRequestMessage, object> reply) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Content);
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply(request)) });
        }
    }
    [Fact]
    public async Task DestinationDiscoveryContactsOnlySavedSeerrAndNeverReturnsKeysOrApprovesAnything()
    {
        using var f = new FeatureFixture();
        using var handler = new Handler(request =>
        {
            Assert.Equal("seerr", request.RequestUri!.Host);
            Assert.Equal("seerr-private-key", request.Headers.GetValues("X-Api-Key").Single());
            return request.RequestUri.AbsolutePath.EndsWith("radarr") ? new object[] { new { id = 7, hostname = "unapproved-arr", port = 7878, baseUrl = "/media", useSsl = false, apiKey = "must-never-leak" } } : Array.Empty<object>();
        });
        using var http = new ServiceHttp(handler); var discovery = new ConfigurationDiscovery(f.Settings, http);
        var revision = f.Settings.Current.Revision; var result = await discovery.Destinations(revision, default);
        var row = Assert.Single(result); Assert.False(row.Approved); Assert.Equal(7, row.Instance); Assert.Equal("http://unapproved-arr:7878/media", row.Url);
        Assert.DoesNotContain("must-never-leak", JsonSerializer.Serialize(result)); Assert.Empty(f.Settings.Current.Features.ArrDestinations);
        Assert.Equal(revision, f.Settings.Current.Revision); Assert.Equal(2, handler.Requests.Count); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task OptionsUseSavedServiceKeyAndExposeOnlyExistingAccessibleRootsAndNamedProfiles()
    {
        using var f = new FeatureFixture(); using var handler = new Handler(request =>
        {
            Assert.Equal("lidarr", request.RequestUri!.Host); Assert.Equal("lidarr-private-key", request.Headers.GetValues("X-Api-Key").Single());
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v1/rootfolder" => new object[] { new { id = 1, path = "/music", accessible = true }, new { id = 2, path = "/unavailable", accessible = false } },
                "/api/v1/qualityprofile" => new[] { new { id = 2, name = "Lossless <img src=x>" } },
                "/api/v1/metadataprofile" => new[] { new { id = 3, name = "Albums" } },
                _ => throw new InvalidOperationException("Unexpected route")
            };
        });
        using var http = new ServiceHttp(handler); var discovery = new ConfigurationDiscovery(f.Settings, http);
        var revision = f.Settings.Current.Revision; var result = await discovery.Options("lidarr", revision, default);
        Assert.Equal("/music", Assert.Single(result.Roots).Value); Assert.Equal("2", Assert.Single(result.QualityProfiles).Value);
        Assert.DoesNotContain("private-key", JsonSerializer.Serialize(result)); Assert.Equal(3, handler.Requests.Count); Assert.Equal(revision, f.Settings.Current.Revision);
    }
    [Theory]
    [InlineData("radarr")]
    [InlineData("http://attacker")]
    [InlineData("../lidarr")]
    public async Task CallerCannotChooseAnArbitraryServiceOrUrl(string service)
    {
        using var f = new FeatureFixture(); using var handler = new Handler(_ => throw new InvalidOperationException("No request expected")); using var http = new ServiceHttp(handler);
        await Assert.ThrowsAsync<HubError>(() => new ConfigurationDiscovery(f.Settings, http).Options(service, f.Settings.Current.Revision, default)); Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task StaleRevisionIsRejectedBeforeAnyServiceContact()
    {
        using var f = new FeatureFixture(); using var handler = new Handler(_ => throw new InvalidOperationException("No request expected")); using var http = new ServiceHttp(handler);
        await Assert.ThrowsAsync<HubError>(() => new ConfigurationDiscovery(f.Settings, http).Destinations(0, default)); Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task RevisionChangeDuringReadPreventsReturningOldPrivateOptions()
    {
        using var f = new FeatureFixture(); var original = f.Settings.Current.Revision;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("metadataprofile")) f.Update(f.Features());
            return request.RequestUri.AbsolutePath.EndsWith("rootfolder") ? new object[] { new { path = "/private", accessible = true } } : new object[] { new { id = 2, name = "Profile" } };
        });
        using var http = new ServiceHttp(handler);
        await Assert.ThrowsAsync<HubError>(() => new ConfigurationDiscovery(f.Settings, http).Options("lidarr", original, default));
    }
    [Fact]
    public async Task DuplicateServiceIdentitiesAreNotPresentedAsChoices()
    {
        using var f = new FeatureFixture(); using var handler = new Handler(_ => new[] { new { id = 7, hostname = "arr", port = 7878, baseUrl = "", useSsl = false }, new { id = 7, hostname = "other", port = 7878, baseUrl = "", useSsl = false } });
        using var http = new ServiceHttp(handler);
        await Assert.ThrowsAsync<HubError>(() => new ConfigurationDiscovery(f.Settings, http).Destinations(f.Settings.Current.Revision, default)); Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task RevokedSavedAddressStopsFurtherDiscoveryCallsImmediately()
    {
        using var f = new FeatureFixture(); var revision = f.Settings.Current.Revision;
        using var handler = new Handler(_ => { f.Update(f.Features()); return new[] { new { path = "/music", accessible = true } }; });
        using var http = new ServiceHttp(handler);
        await Assert.ThrowsAsync<HubError>(() => new ConfigurationDiscovery(f.Settings, http).Options("lidarr", revision, default));
        Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task NativeOrdinaryUserCannotReadAdministratorPathsEvenWithoutMvcPolicyExecution()
    {
        using var f = new FeatureFixture(); using var handler = new Handler(_ => throw new InvalidOperationException("No request expected")); using var http = new ServiceHttp(handler);
        using var services = new ServiceCollection().AddSingleton(f.Identity).AddSingleton(new ConfigurationDiscovery(f.Settings, http)).BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("Jellyfin-UserId", f.User.Id.ToString()), new("Jellyfin-DeviceId", "device"), new("Jellyfin-IsApiKey", "False"), new("Jellyfin-Token", "token")], "Jellyfin"));
        var controller = new ConfigurationController(services) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = principal } } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await controller.Options("lidarr", f.Settings.Current.Revision, default)).StatusCode); Assert.Empty(handler.Requests);
    }
}
