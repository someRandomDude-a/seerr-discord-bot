using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Lazy, bounded, failure-contained personal reconciliation. Never requests or removes media.</summary>
public sealed class MediaMaintenance(IServiceProvider services, ILogger<MediaMaintenance> log) : BackgroundService
{
    private string cursor = "";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // No integration work on installation/startup; existing users are serviced after the first interval.
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); budget.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var store = services.GetRequiredService<HubStore>(); var identity = services.GetRequiredService<HostIdentity>();
                    var users = new List<string>();
                    using (var db = store.Open()) using (var read = HubStore.Command(db, "SELECT user FROM (SELECT user FROM links UNION SELECT DISTINCT user FROM subscriptions UNION SELECT DISTINCT user FROM external_watches UNION SELECT DISTINCT user FROM extra_watches UNION SELECT DISTINCT user FROM watchlist_intents) WHERE user>$0 ORDER BY user LIMIT 10", cursor))
                    using (var r = read.ExecuteReader()) while (r.Read()) users.Add(r.GetString(0));
                    if (users.Count == 0) cursor = "";
                    foreach (var key in users)
                    {
                        budget.Token.ThrowIfCancellationRequested(); cursor = key;
                        try
                        {
                            var user = await identity.Linked(Guid.Parse(key));
                            await services.GetRequiredService<WatchlistSync>().Sync(user, async () =>
                            { if ((await identity.Linked(user.Id)).Id != user.Id) throw new HubError("Account changed."); }, budget.Token);
                        }
                        catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
                        catch { /* Failed or partial reads leave local state/intents intact. */ }
                        try { await services.GetRequiredService<NotificationFeed>().Scan(identity.Active(Guid.Parse(key)), budget.Token); }
                        catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
                        catch { /* An outage does not become an availability change. */ }
                    }
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested && !stoppingToken.IsCancellationRequested) { }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch { log.LogWarning("Media Hub personal updates unavailable. Existing state was retained."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { log.LogWarning("Media Hub personal worker stopped after a plugin failure. Jellyfin remains available."); }
    }
}
