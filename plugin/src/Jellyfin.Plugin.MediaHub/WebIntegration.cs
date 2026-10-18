using System.Reflection;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.MediaHub;

public sealed record WebDependencyStatus(string Id, string Name, string RequiredVersion, string? LoadedVersion, bool ApiCompatible, bool Registered, string State);
public sealed record WebIntegrationStatus(bool Required, bool RegistrationsReady, string TargetAbi, IReadOnlyList<WebDependencyStatus> Dependencies);

/// <summary>Required, separately installed native Web plugins. No downloads, host edits or provider engines.</summary>
public sealed class WebIntegration
{
    public const string TestedVersion = "3.0.2.0";
    public const string TransformationMinimum = "2.2.1.0";
    public const string PagesMinimum = "3.0.0.0";
    public const string TransformationId = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90";
    public const string PagesId = "5b6550fa-a014-4f4c-8a2c-59a43680ac6d";
    private readonly object gate = new();
    private readonly Dictionary<string, bool> registrations = new();
    private readonly HashSet<string> failed = new();
    private static Type? Find(string name) => AssemblyLoadContext.All.SelectMany(context => context.Assemblies).Select(a => a.GetType(name)).FirstOrDefault(t => t is not null);
    internal static MethodInfo? Registration(Type type, bool transformation) => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .SingleOrDefault(method => method.Name == (transformation ? "RegisterTransformation" : "RegisterPage") && method.ReturnType == typeof(void)
            && method.GetParameters() is [{ ParameterType.FullName: "Newtonsoft.Json.Linq.JObject" }]);
    internal static void Register(Type type, bool transformation, JObject payload)
    {
        var method = Registration(type, transformation) ?? throw new InvalidOperationException("Unsupported Web dependency contract");
        // Newtonsoft may be loaded separately by each plugin. Construct the payload in
        // the provider's load context rather than comparing CLR JObject identities.
        var parameter = method.GetParameters()[0].ParameterType;
        var value = parameter.IsInstanceOfType(payload) ? payload : parameter.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])?.Invoke(null, [payload.ToString(Newtonsoft.Json.Formatting.None)]);
        if (value is null) throw new InvalidOperationException("Unsupported Web dependency payload");
        method.Invoke(null, [value]);
    }
    internal static bool Contract(Type? type, Version? version, bool transformation)
    {
        if (type is null || version is null || version < Version.Parse(transformation ? TransformationMinimum : PagesMinimum)) return false;
        var register = Registration(type, transformation);
        var remove = type.GetMethod(transformation ? "RemoveTransformation" : "RemovePage", BindingFlags.Public | BindingFlags.Static, [transformation ? typeof(Guid) : typeof(string)]);
        return register?.ReturnType == typeof(void) && remove?.ReturnType == typeof(void);
    }
    private static Type? Api(bool transformation)
    {
        var type = Find(transformation ? "Jellyfin.Plugin.FileTransformation.PluginInterface" : "Jellyfin.Plugin.PluginPages.PluginInterface");
        return Contract(type, type?.Assembly.GetName().Version, transformation) ? type : null;
    }
    internal Type? TransformationApi() => Api(true);
    internal Type? PagesApi() => Api(false);
    internal void Registered(string id) { lock (gate) { registrations[id] = true; failed.Remove(id); } }
    internal void Unavailable(string id, bool failure = false) { lock (gate) { registrations[id] = false; if (failure) failed.Add(id); else failed.Remove(id); } }
    public WebIntegrationStatus Status()
    {
        lock (gate)
        {
            WebDependencyStatus Describe(bool transformation)
            {
                var id = transformation ? TransformationId : PagesId; var name = transformation ? "File Transformation" : "Plugin Pages";
                var type = Find(transformation ? "Jellyfin.Plugin.FileTransformation.PluginInterface" : "Jellyfin.Plugin.PluginPages.PluginInterface");
                var version = type?.Assembly.GetName().Version; var compatible = Contract(type, version, transformation); var registered = compatible && registrations.GetValueOrDefault(id);
                return new(id, name, transformation ? TransformationMinimum : PagesMinimum, version?.ToString(), compatible, registered, type is null ? "missing" : !compatible ? "incompatible" : registered ? "registered" : failed.Contains(id) ? "registration_failed" : "not_registered");
            }
            var dependencies = new[] { Describe(true), Describe(false) };
            return new(true, dependencies.All(d => d.Registered), "12.2.0", dependencies);
        }
    }
}
