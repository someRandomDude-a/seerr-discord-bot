using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WatchlistDeletion.Api;

/// <summary>Trusted in-process integrations only. Never expose subject/operation selection to an unauthenticated client.</summary>
public interface IWatchlistDeletionClient
{
    bool IsAvailable { get; }
    Task<T> InvokeAsync<T>(string subject, Guid? expectedUser, string operation, object? payload = null, CancellationToken cancellation = default);
}
public sealed class CoreApiException(string message, string code) : Exception(message) { public string Code { get; } = code; }
public static class CoreApi
{
    public const string ServiceKey = "Jellyfin.Plugin.WatchlistDeletion.Api.1.0";
    public const string DiscordTransportKey = "Jellyfin.Plugin.MediaHub.Discord.Transport.1.0";
    public const string DiscordReadyKey = "Jellyfin.Plugin.MediaHub.Discord.Ready.1.0";
    public const string DiscordConfigurationKey = "Jellyfin.Plugin.MediaHub.Discord.Configuration.1.0";
    public static IServiceCollection AddWatchlistDeletionClient(this IServiceCollection services) => services.AddSingleton<IWatchlistDeletionClient, CoreClient>();
}
public sealed record CoreRequest(Guid? ExpectedUser, JsonElement Payload);
public sealed record CoreReply(bool Success, JsonElement Value, string? Error = null, string? Code = null);
internal sealed class CoreClient(IServiceProvider services) : IWatchlistDeletionClient
{
    private readonly SemaphoreSlim slots = new(8);
    private Func<string, string, string, CancellationToken, Task<string>>? Provider => services.GetKeyedService<Func<string, string, string, CancellationToken, Task<string>>>(CoreApi.ServiceKey);
    public bool IsAvailable => Provider is not null;
    public async Task<T> InvokeAsync<T>(string subject, Guid? expectedUser, string operation, object? payload = null, CancellationToken cancellation = default)
    {
        var provider = Provider ?? throw new CoreApiException("Install the matching Watchlist Deletion plugin and restart Jellyfin.", "core_unavailable");
        if (!await slots.WaitAsync(0, cancellation)) throw new CoreApiException("An integration operation is still pending. No second operation was started.", "core_busy");
        Task<string>? pending = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var json = JsonSerializer.Serialize(new CoreRequest(expectedUser, JsonSerializer.SerializeToElement(payload ?? new { })));
            pending = Task.Run(() => { budget.Token.ThrowIfCancellationRequested(); return provider(subject, operation, json, budget.Token); }, CancellationToken.None);
            var response = await pending.WaitAsync(budget.Token);
            if (response.Length > 24 * 1024 * 1024) throw new CoreApiException("Integration result exceeded its budget.", "core_unavailable");
            var reply = JsonSerializer.Deserialize<CoreReply>(response) ?? throw new CoreApiException("Unverifiable integration result.", "core_unavailable");
            if (!reply.Success) throw new CoreApiException(reply.Error ?? "Operation was not confirmed.", reply.Code ?? "core_rejected");
            return reply.Value.Deserialize<T>()!;
        }
        catch (CoreApiException) { throw; }
        catch { throw new CoreApiException("Operation not confirmed. Inspect current state before acting again; no automatic retry was sent.", "core_uncertain"); }
        finally
        {
            if (pending is { IsCompleted: false }) _ = pending.ContinueWith(t => { _ = t.Exception; slots.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else { _ = pending?.Exception; slots.Release(); } // Observe a fault racing with cancellation too.
        }
    }
}
