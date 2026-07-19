using System.Collections.Concurrent;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;

namespace RateLimiter.Infrastructure.FailSafe;

/// A same-algorithm sliding-window rate limiter that lives entirely in this
/// process's memory - no Redis, no network calls. This is the "fail-safe
/// strategy" required by the brief: if Redis is temporarily unreachable,
/// we do NOT block all traffic. Instead, each api instance falls back to
/// enforcing the limit locally, using its own memory.

/// IMPORTANT, DELIBERATE TRADE-OFF (explain this in interviews/README):
/// This is NOT cluster-accurate. If Redis is down and all 3 api instances
/// fall back simultaneously, each one enforces the FULL configured limit
/// independently. In the worst case, a client could get up to
/// (limit × number of instances) requests through during the outage
/// instead of just `limit`.

public sealed class LocalMemoryRateLimiter : IRateLimiterRepository
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _windows = new();

    private readonly IClientPolicyStoreRepository _policyStore;

    public LocalMemoryRateLimiter(IClientPolicyStoreRepository policyStore)
    {
        _policyStore = policyStore;
    }

    public async Task<RateLimitResult> CheckAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var policy = await _policyStore.GetPolicyAsync(clientId, cancellationToken);
        var queue = _windows.GetOrAdd(clientId, _ => new ConcurrentQueue<long>());

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = policy.WindowSeconds * 1000L;
        var windowStart = nowMs - windowMs;

        // We lock on the queue instance itself for the check-and-enqueue.
        // This is a plain in-memory lock (sub-microsecond), completely
        // different from the network round-trip we're trying to avoid -
        // it's what keeps this fallback fast even under concurrency.
        lock (queue)
        {
            // Evict entries that have aged out of the window.
            while (queue.TryPeek(out var oldest) && oldest < windowStart)
            {
                queue.TryDequeue(out _);
            }

            if (queue.Count < policy.LimitPerWindow)
            {
                queue.Enqueue(nowMs);
                var remaining = policy.LimitPerWindow - queue.Count;
                return RateLimitResult.Allowed(remaining, policy.LimitPerWindow, isFailSafe: true);
            }

            queue.TryPeek(out var head);
            var retryAfterMs = windowMs - (nowMs - head);
            var retryAfterSeconds = (int)Math.Ceiling(Math.Max(retryAfterMs, 0) / 1000.0);
            return RateLimitResult.Blocked(policy.LimitPerWindow, retryAfterSeconds);
        }
    }
}
