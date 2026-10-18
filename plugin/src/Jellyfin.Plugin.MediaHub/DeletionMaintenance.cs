using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Re-evaluates consent/aging and runs opt-in, checkpointed Arr operations while users are offline.</summary>
public sealed class DeletionMaintenance(IServiceProvider services, ILogger<DeletionMaintenance> log) : BackgroundService
{
    internal async Task Review(CancellationToken cancellation)
    {
        var queue = services.GetRequiredService<DeletionQueue>();
        var identity = services.GetRequiredService<HostIdentity>();
        foreach (var owner in queue.Owners())
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                // Native proposals remain authoritative while their owner is offline.
                // Discord linking is neither a permission nor a lifetime dependency.
                var user = identity.Active(owner);
                await queue.Refresh(user, cancellation);
                await queue.Execute(user, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch { queue.Block(owner); }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); budget.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    await Review(budget.Token);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                catch { log.SafeWarning("Media Hub deletion review unavailable. No deletion was attempted."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { log.SafeWarning("Media Hub deletion review stopped. Jellyfin remains available."); }
    }
}
