using Jellyfin.Plugin.MediaHub;
using Newtonsoft.Json.Linq;
using Xunit;
using System.Runtime.Loader;

namespace MediaHub.Tests;

public sealed class WebIntegrationTests
{
    public static class PagesApi
    {
        public static JObject? LastPayload;
        public static void RegisterPage(JObject payload) { LastPayload = payload; }
        public static void RemovePage(string id) { }
    }
    [Fact]
    public void RegistrationConstructsJsonInTheReceivingPluginsSeparateLoadContext()
    {
        var context = new AssemblyLoadContext("mediahub-web-contract-test", isCollectible: true);
        try
        {
            context.LoadFromAssemblyPath(typeof(JObject).Assembly.Location);
            var assembly = context.LoadFromAssemblyPath(typeof(WebIntegrationTests).Assembly.Location);
            var api = assembly.GetType(typeof(PagesApi).FullName!)!;
            Assert.NotEqual(typeof(JObject), WebIntegration.Registration(api, false)!.GetParameters()[0].ParameterType);
            Assert.True(WebIntegration.Contract(api, new(3, 0, 2, 0), false));
            WebIntegration.Register(api, false, JObject.FromObject(new { displayText = "Media Hub" }));
            var received = api.GetField("LastPayload")!.GetValue(null);
            Assert.NotNull(received); Assert.Equal("Media Hub", JObject.Parse(received.ToString()!)["displayText"]!.Value<string>());
        }
        finally { context.Unload(); }
    }
    public static class TransformationApi
    {
        public static void RegisterTransformation(JObject payload) { }
        public static void RemoveTransformation(Guid id) { }
    }
    public static class WrongApi
    {
        public static bool RegisterPage(JObject payload) => true;
        public static void RemovePage(Guid id) { }
    }
    [Fact]
    public void RequiredContractsUseUpstreamMinimumsAndPublicRegistrationCleanupShapesNotExactPins()
    {
        Assert.True(WebIntegration.Contract(typeof(PagesApi), new(3, 0, 2, 0), false));
        Assert.True(WebIntegration.Contract(typeof(TransformationApi), new(3, 0, 2, 0), true));
        Assert.True(WebIntegration.Contract(typeof(PagesApi), new(3, 0, 1, 0), false));
        Assert.True(WebIntegration.Contract(typeof(PagesApi), new(4, 0, 0, 0), false));
        Assert.False(WebIntegration.Contract(typeof(PagesApi), new(2, 4, 1, 0), false));
        Assert.True(WebIntegration.Contract(typeof(TransformationApi), new(2, 2, 1, 0), true));
        Assert.False(WebIntegration.Contract(typeof(TransformationApi), new(2, 2, 0, 0), true));
        Assert.False(WebIntegration.Contract(typeof(WrongApi), new(3, 0, 2, 0), false));
        Assert.False(WebIntegration.Contract(null, null, true));
    }
    [Fact]
    public void MissingProvidersAreExplicitDependenciesAndCannotBeClaimedReadyByRegistrationFlags()
    {
        var dependencies = new WebIntegration(); dependencies.Registered(WebIntegration.TransformationId); dependencies.Registered(WebIntegration.PagesId);
        var state = dependencies.Status(); Assert.True(state.Required); Assert.False(state.RegistrationsReady); Assert.Equal("12.2.0", state.TargetAbi);
        Assert.Equal(2, state.Dependencies.Count); Assert.All(state.Dependencies, dependency => { Assert.Equal("missing", dependency.State); Assert.False(dependency.Registered); Assert.False(dependency.ApiCompatible); });
    }
    [Fact]
    public void NativeMenuEntriesStayInsideMediaHubsIdentityAndNeverReplaceOtherProvidersPages()
    {
        Assert.Equal(3, PluginPageBridge.PageIds.Length); Assert.Equal(3, PluginPageBridge.PageIds.Distinct().Count());
        Assert.All(PluginPageBridge.PageIds, id => Assert.StartsWith("media-hub-c70297bf-77bd-4ad4-93c9-f67598519777", id));
    }
}
