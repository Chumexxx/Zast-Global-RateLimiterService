using Microsoft.Extensions.Hosting;

namespace RateLimiter.Infrastructure.Policies;

/// Periodically reloads client rate-limit policies from Postgres into
/// PostgresClientPolicyStore's in-memory cache, so a support engineer
/// running `UPDATE client_policies SET limit_per_window = ...` takes
/// effect across the whole cluster within one refresh interval - no
/// redeploy, no restart.
public sealed class PolicyCacheRefreshWorker : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly PostgresClientPolicyStore _store;

    public PolicyCacheRefreshWorker(PostgresClientPolicyStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load once immediately at startup so the very first requests after boot already see real Postgres-backed policies, instead
        // of falling back to defaults for the first 30 seconds.
        await _store.RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _store.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
    }
}
