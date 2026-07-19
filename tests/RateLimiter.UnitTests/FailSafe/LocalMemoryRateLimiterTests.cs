using FluentAssertions;
using RateLimiter.Infrastructure.FailSafe;
using RateLimiter.UnitTests.TestDoubles;
using Xunit;

namespace RateLimiter.UnitTests.FailSafe;

/// Pure, fast, in-memory tests - no Redis, no Docker, no network. These
/// run in milliseconds, which matters: LocalMemoryRateLimiter is the code
/// path that MUST keep working when everything else (Redis, Postgres) is
/// down, so its own tests shouldn't depend on any external system either.
public sealed class LocalMemoryRateLimiterTests
{
    [Fact]
    public async Task CheckAsync_AllowsUpToTheLimitThenBlocks()
    {
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 2, windowSeconds: 60);
        var limiter = new LocalMemoryRateLimiter(policyStore);
        const string clientId = "client-x";

        var first = await limiter.CheckAsync(clientId);
        var second = await limiter.CheckAsync(clientId);
        var third = await limiter.CheckAsync(clientId);

        first.IsAllowed.Should().BeTrue();
        second.IsAllowed.Should().BeTrue();
        third.IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_MarksEveryResultAsFailSafe()
    {
        // Callers (and, in the real system, the /api/v1/ratelimit response
        // headers and dashboard) rely on this flag to distinguish "the
        // accurate Redis path answered" from "the degraded fallback
        // answered" - it must always be true for this class.
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 5, windowSeconds: 60);
        var limiter = new LocalMemoryRateLimiter(policyStore);

        var result = await limiter.CheckAsync("client-y");

        result.IsFailSafe.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_TreatsDifferentClientsIndependently()
    {
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 1, windowSeconds: 60);
        var limiter = new LocalMemoryRateLimiter(policyStore);

        var aFirst = await limiter.CheckAsync("client-a");
        var aSecond = await limiter.CheckAsync("client-a");
        var bFirst = await limiter.CheckAsync("client-b");

        aFirst.IsAllowed.Should().BeTrue();
        aSecond.IsAllowed.Should().BeFalse();
        bFirst.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_UnderConcurrentLoadWithinASingleProcess_NeverExceedsTheLimit()
    {
        // LocalMemoryRateLimiter is explicitly NOT cluster-safe (see its
        // class-level doc comment) - but it MUST still be thread-safe
        // WITHIN one process, since multiple concurrent requests can hit
        // the same instance even without a cluster involved.
        const int limit = 10;
        const int concurrentRequests = 100;

        var policyStore = new FixedClientPolicyStore(limitPerWindow: limit, windowSeconds: 60);
        var limiter = new LocalMemoryRateLimiter(policyStore);
        const string clientId = "concurrent-client";

        var tasks = Enumerable.Range(0, concurrentRequests).Select(_ => limiter.CheckAsync(clientId));
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.IsAllowed).Should().Be(limit);
    }
}
