using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using RateLimiter.Infrastructure.Persistence;

namespace RateLimiter.Infrastructure.Policies;

/// The production IClientPolicyStore: per-client rate limits live in
/// Postgres (see db/init.sql's client_policies table), so changing a
/// client's limit is a SQL UPDATE, not a redeploy.
///
/// CRITICAL DESIGN POINT: <see cref="GetPolicyAsync"/> NEVER queries
/// Postgres directly. It only ever reads from an in-memory dictionary
/// cache. If it queried Postgres on every call, we'd be adding a database
/// round-trip to the rate limiter's hot path - exactly what we avoided
/// with Redis for the counting itself. Instead, the cache is loaded once
/// at startup and refreshed periodically in the background by
/// <see cref="PolicyCacheRefreshWorker"/>.
///
/// FAIL-SAFE NOTE: if a refresh fails (Postgres is briefly unreachable),
/// we keep serving the last successfully loaded cache rather than clearing
/// it. Stale-but-present policy data beats no policy data.
///
/// UNKNOWN CLIENT BEHAVIOR: returns null for any client ID not present in
/// the cache.
public sealed class PostgresClientPolicyStore : IClientPolicyStoreRepository
{
    private readonly IDbContextFactory<RateLimiterDbContext> _dbContextFactory;
    private readonly ILogger<PostgresClientPolicyStore> _logger;

    // `volatile` + swapping the whole reference on refresh (rather than
    // mutating the dictionary in place) means readers never observe a
    // half-updated cache, without needing a lock on the read path at all.
    private volatile Dictionary<string, ClientPolicy> _cache = new();

    public PostgresClientPolicyStore(
        IDbContextFactory<RateLimiterDbContext> dbContextFactory,
        ILogger<PostgresClientPolicyStore> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public Task<ClientPolicy?> GetPolicyAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var cache = _cache; // local reference - stable even if RefreshAsync swaps _cache concurrently

        return Task.FromResult(cache.GetValueOrDefault(clientId));
    }

    /// Reloads the entire policy cache from Postgres. Called once at
    /// startup and then on a fixed interval by PolicyCacheRefreshWorker.
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var policies = await db.ClientPolicies
                .AsNoTracking() // read-only query - skip EF's change tracking overhead entirely
                .ToListAsync(cancellationToken);

            var newCache = policies.ToDictionary(
                p => p.ClientId,
                p => new ClientPolicy
                {
                    ClientId = p.ClientId,
                    LimitPerWindow = p.LimitPerWindow,
                    WindowSeconds = p.WindowSeconds
                });

            _cache = newCache; // atomic reference swap

            _logger.LogInformation("Refreshed client policy cache with {Count} client(s).", newCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to refresh client policy cache from Postgres. Continuing to serve the " +
                "last known policies ({Count} client(s)) until the next refresh attempt.",
                _cache.Count);
        }
    }
}