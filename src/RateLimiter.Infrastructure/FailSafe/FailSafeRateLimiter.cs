using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using StackExchange.Redis;

namespace RateLimiter.Infrastructure.FailSafe;

/// The IRateLimiterRepository that the rest of the application actually talks to.
/// Implements the Decorator pattern: it tries the accurate Redis-backed
/// limiter first, and transparently falls back to the local in-memory limiter if Redis is unreachable or too slow.
///

public sealed class FailSafeRateLimiter : IRateLimiterRepository
{
    // If Redis hasn't responded within this budget, we stop waiting and
    // use the fallback for THIS request. 75ms is generous compared to
    // Redis's typical sub-millisecond response time on a healthy local
    // network - it's meant to catch "Redis is struggling/unreachable",
    // not to be a tight performance target in the happy path.
    private static readonly TimeSpan RedisTimeout = TimeSpan.FromMilliseconds(75);

    private readonly IRateLimiterRepository _primary;
    private readonly IRateLimiterRepository _fallback;
    private readonly ILogger<FailSafeRateLimiter> _logger;

    public FailSafeRateLimiter( [FromKeyedServices("primary")] IRateLimiterRepository primary, [FromKeyedServices("fallback")] IRateLimiterRepository fallback,
        ILogger<FailSafeRateLimiter> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<RateLimitResult> CheckAsync(string clientId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Task.WaitAsync wraps the call with a timeout WITHOUT requiring the underlying Redis client call to support cancellation
            // itself (StackExchange.Redis's async calls don't observe a CancellationToken mid-flight). If the timeout elapses first,
            // this throws TimeoutException and we move on to the fallback - we simply stop waiting for Redis's answer, we don't need it
            // to actually cancel the in-flight command.
            return await _primary
                .CheckAsync(clientId, cancellationToken)
                .WaitAsync(RedisTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException or RedisServerException)
        {
            _logger.LogWarning(ex,
                "Redis error while checking rate limit for client {ClientId}. Falling back to local in-memory rate limiting for this request.",
                clientId);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex,
                "Redis did not respond within {TimeoutMs}ms for client {ClientId}. Falling back to local in-memory rate limiting for this request.",
                RedisTimeout.TotalMilliseconds, clientId);
        }
        // Deliberately NOT catching OperationCanceledException here beyond
        // what WaitAsync already does with the caller's own token - if the
        // caller cancelled the request themselves, we let that propagate
        // normally instead of masking it as a Redis failure.

        return await _fallback.CheckAsync(clientId, cancellationToken);
    }
}
