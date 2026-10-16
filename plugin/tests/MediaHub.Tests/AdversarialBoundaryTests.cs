using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.MediaHub;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Moq;
using Xunit;

namespace MediaHub.Tests;

public sealed class AdversarialBoundaryTests
{
    [Theory]
    [InlineData("{\"id\":1,\"id\":2}")]
    [InlineData("{\"nested\":{\"id\":1,\"ID\":2}}")]
    [InlineData("{\"id\":1,\"\\u0069d\":2}")]
    [InlineData("{\"id\":NaN}")]
    [InlineData("{}{}")]
    [InlineData("{\"id\":1,}")]
    [InlineData("null")]
    public void AmbiguousOrInvalidJsonIsRejected(string json) => Assert.Throws<HubError>(() => SafeJson.Parse(json));

    [Fact]
    public void JsonDepthSizeAndStringBudgetsAreEnforced()
    {
        Assert.Throws<HubError>(() => SafeJson.Parse(new string('[', 40) + "0" + new string(']', 40)));
        Assert.Throws<HubError>(() => SafeJson.Parse("{\"text\":\"" + new string('a', 131073) + "\"}"));
        Assert.Throws<HubError>(() => SafeJson.Parse("{\"a\":1234}", 8));
        Assert.Equal("A & B", SafeJson.Parse("{\"text\":\"A & B\",\"items\":[1,2]}")["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("file:///config/data/jellyfin.db")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/base/../api")]
    [InlineData("https://example.com/%2e%2e/api")]
    [InlineData("https://example.com/%252fapi")]
    [InlineData("https://example.com/base%2fother")]
    [InlineData("https://example.com/base?key=secret")]
    [InlineData("https://example.com\\@other.invalid/")]
    [InlineData("https://example.com/\r\nAuthorization: secret")]
    public void AmbiguousServiceAddressesCannotBeUsed(string address) => Assert.Throws<HubError>(() => ServiceAddress.Url(address));

    [Fact]
    public void ExplicitPrivateDestinationsRemainSupportedButHeadersAndEscapingRoutesDoNot()
    {
        Assert.Equal("http://127.0.0.1:7878/radarr", ServiceAddress.Url("http://127.0.0.1:7878/radarr/"));
        Assert.Equal("http://[::1]:8989/sonarr", ServiceAddress.Discovered("::1", 8989, "/sonarr", false));
        Assert.Throws<HubError>(() => ServiceAddress.Key("key\r\nX-API-User: 1"));
        Assert.Throws<HubError>(() => ServiceAddress.Route("api/v1/%2e%2e/command"));
        Assert.Throws<HubError>(() => ServiceAddress.Route("api/v1/movie#ignored"));
        ServiceAddress.Route("api/v1/search?query=%2F%23%3F");
    }

    [Fact]
    public void SettingsFlagsAndDestinationsAreAuthenticatedAndTamperingDoesNotResetFiles()
    {
        using var f = new FeatureFixture(); var file = Path.Combine(f.Directory, "settings.json");
        var node = JsonNode.Parse(File.ReadAllText(file))!; node["AutomaticDeletion"] = true; var malicious = node.ToJsonString(); File.WriteAllText(file, malicious);
        Assert.Throws<HubError>(() => _ = f.Settings.Current);
        Assert.Throws<HubError>(() => new SettingsService(f.Store));
        Assert.Equal(malicious, File.ReadAllText(file)); f.Data.VerifyNoOtherCalls();
    }
    [Fact]
    public void AValidOldSettingsFileCannotSilentlyRollBackALiveInstancesAuthorization()
    {
        using var f = new FeatureFixture(); var path = Path.Combine(f.Directory, "settings.json"); var older = File.ReadAllText(path);
        f.Update(f.Features() with { Operators = [] }); File.WriteAllText(path, older);
        Assert.Throws<HubError>(() => _ = f.Settings.Current); Assert.Equal(older, File.ReadAllText(path));
    }
    [Fact]
    public void MalformedCompanionPayloadsCannotThrowIntoTheHostsWebTransformPipeline()
    {
        Assert.Equal("", ItemControlsRegistration.Transform(new Newtonsoft.Json.Linq.JObject { ["contents"] = new Newtonsoft.Json.Linq.JObject() }));
        var original = "<html><body>" + new string('x', 4 * 1024 * 1024) + "</body></html>";
        Assert.Equal(original, ItemControlsRegistration.Transform(new Newtonsoft.Json.Linq.JObject { ["contents"] = original }));
    }

    [Fact]
    public void UnsignedOldSettingsAreReviewableButCannotActivateIntegrations()
    {
        using var f = new FeatureFixture(); var file = Path.Combine(f.Directory, "settings.json");
        var node = JsonNode.Parse(File.ReadAllText(file))!; node["FormatVersion"] = 2; node.AsObject().Remove("Integrity"); File.WriteAllText(file, node.ToJsonString());
        var legacy = new SettingsService(f.Store); Assert.Throws<HubError>(() => _ = legacy.Current);
        var visible = JsonSerializer.SerializeToNode(legacy.Public())!; Assert.True(visible["ConfigurationReviewRequired"]!.GetValue<bool>()); Assert.False(visible["DiscordEnabled"]!.GetValue<bool>());
        Assert.False(visible["Features"]!["Lidarr"]!["Requests"]!.GetValue<bool>());
        legacy.Update(new(visible["Revision"]!.GetValue<int>(), false, "https://reviewed.example", [], "", null, null));
        Assert.Equal(3, legacy.Current.FormatVersion); Assert.False(legacy.Current.DiscordEnabled); Assert.False(legacy.Current.Features.Lidarr.Requests);
    }

    [Fact]
    public async Task CopyingAnEncryptedNativeCredentialToAnotherIdentityCannotImpersonateTheVictim()
    {
        using var f = new FeatureFixture();
        using (var db = f.Store.Open()) using (var change = HubStore.Command(db, "UPDATE links SET discord='99' WHERE discord='42'")) change.ExecuteNonQuery();
        await Assert.ThrowsAsync<HubError>(() => f.Identity.Discord("99"));
        f.Sessions.Verify(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        f.Data.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResourceBoundaryRejectsDuplicateAndChunkedOversizedBodiesBeforeActionsRun()
    {
        var boundary = new HubRequestBoundary();
        foreach (var body in new[] { "{\"Mode\":\"normal\",\"mode\":\"announcement\"}", new string(' ', 65537) })
        {
            var context = Context(body); var reached = false;
            await boundary.OnResourceExecutionAsync(context, () => { reached = true; return Task.FromResult(new ResourceExecutedContext(context, [])); });
            Assert.False(reached); Assert.IsType<BadRequestObjectResult>(context.Result);
        }
        var valid = Context("{}"); await boundary.OnResourceExecutionAsync(valid, () => Task.FromResult(new ResourceExecutedContext(valid, [])));
        Assert.Null(valid.Result); // Admission was released after every invalid request.
    }

    [Fact]
    public async Task ResourceAdmissionIsBoundedAndModelBindingErrorsNeverEchoSecretValues()
    {
        var boundary = new HubRequestBoundary(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var entered = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => { var context = Context("{}"); return boundary.OnResourceExecutionAsync(context, async () => { Interlocked.Increment(ref entered); await release.Task; return new ResourceExecutedContext(context, []); }); }).ToArray();
        Assert.Equal(8, entered); var ninth = Context("{}");
        await boundary.OnResourceExecutionAsync(ninth, () => throw new InvalidOperationException("Not admitted"));
        Assert.Equal(429, Assert.IsType<ObjectResult>(ninth.Result).StatusCode); release.SetResult(); await Task.WhenAll(tasks);
        var resource = Context("{}"); var action = new ActionExecutingContext(resource, [], new Dictionary<string, object?>(), new object()); action.ModelState.AddModelError("secret", "private-secret-value");
        await boundary.OnActionExecutionAsync(action, () => throw new InvalidOperationException("Invalid binding must not execute"));
        Assert.DoesNotContain("private-secret-value", JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(action.Result).Value));
    }

    [Fact]
    public async Task UnapprovedDiscoveryNeverConnectsToTheAdvertisedArrServer()
    {
        using var f = new FeatureFixture(); var handler = new DiscoveryHandler(); using var http = new ServiceHttp(handler);
        var registry = new ArrRegistry(f.Settings, http);
        await Assert.ThrowsAsync<HubError>(() => registry.Storage(CancellationToken.None));
        Assert.All(handler.Hosts, host => Assert.Equal("seerr", host));
    }

    private static ResourceExecutingContext Context(string body)
    {
        var http = new DefaultHttpContext(); http.Request.ContentType = "application/json"; http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return new(new ActionContext(http, new RouteData(), new ActionDescriptor(), new ModelStateDictionary()), [], []);
    }
    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public readonly List<string> Hosts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Hosts.Add(request.RequestUri!.Host); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { id = 0, hostname = "unapproved-target", port = 7878, baseUrl = "", useSsl = false, apiKey = "upstream-supplied-secret" } }) }); }
    }
}
