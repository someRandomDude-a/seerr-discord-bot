using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHub;

/// <summary>Lazy, bounded, failure-contained personal reconciliation. Never requests or removes media.</summary>
public sealed class MediaMaintenance(IServiceProvider services, ILogger<MediaMaintenance> log, NotificationSignals signals) : BackgroundService
{
    private string cursor = "";
    private DateTimeOffset nextPoll = DateTimeOffset.UtcNow.AddMinutes(1);
    internal static IReadOnlyList<string> KnownUsers(HubStore store, string after)
    {
        using var db = store.Open(); using var read = HubStore.Command(db, "SELECT user FROM (SELECT user FROM links UNION SELECT user FROM personal UNION SELECT user FROM request_operations UNION SELECT user FROM extra_requests UNION SELECT DISTINCT user FROM subscriptions UNION SELECT DISTINCT user FROM external_watches UNION SELECT DISTINCT user FROM extra_watches UNION SELECT DISTINCT user FROM watchlist_intents) WHERE user>$0 ORDER BY user LIMIT 10", after);
        using var r = read.ExecuteReader(); var users = new List<string>(); while (r.Read()) users.Add(r.GetString(0)); return users;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // No integration work on installation/startup; existing users are serviced after the first interval.
            while (true)
            {
                var delay = nextPoll - DateTimeOffset.UtcNow;
                var notificationOnly = delay > TimeSpan.Zero && await signals.Wait(delay, stoppingToken);
                if (DateTimeOffset.UtcNow >= nextPoll) { notificationOnly = false; nextPoll = DateTimeOffset.UtcNow.AddMinutes(1); }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); budget.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    var store = services.GetRequiredService<HubStore>(); var identity = services.GetRequiredService<HostIdentity>();
                    var users = KnownUsers(store, cursor);
                    if (users.Count == 0) cursor = "";
                    foreach (var key in users)
                    {
                        budget.Token.ThrowIfCancellationRequested(); cursor = key;
                        try
                        {
                            var user = identity.Active(Guid.Parse(key));
                            if (!notificationOnly) await services.GetRequiredService<WatchlistSync>().Sync(user, async () =>
                            { identity.Active(user.Id); await Task.CompletedTask; }, budget.Token, importNative: false);
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
                catch { log.SafeWarning("Media Hub personal updates unavailable. Existing state was retained."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { log.SafeWarning("Media Hub personal worker stopped after a plugin failure. Jellyfin remains available."); }
    }
}
