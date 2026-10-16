using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Re-evaluates consent/aging and runs opt-in, checkpointed Arr operations while users are offline.</summary>
public sealed class DeletionMaintenance(IServiceProvider services, ILogger<DeletionMaintenance> log) : BackgroundService
{
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
                    var queue = services.GetRequiredService<DeletionQueue>();
                    var identity = services.GetRequiredService<HostIdentity>();
                    foreach (var owner in queue.Owners())
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        try
                        {
                            var user = await identity.Linked(owner);
                            await queue.Refresh(user, budget.Token);
                            await queue.Execute(user, budget.Token);
                        }
                        catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
                        catch { queue.Block(owner); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                catch { log.LogWarning("Media Hub deletion review unavailable. No deletion was attempted."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { log.LogWarning("Media Hub deletion review stopped. Jellyfin remains available."); }
    }
}
