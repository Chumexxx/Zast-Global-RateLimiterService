using FluentAssertions;
using RateLimiter.Infrastructure.Redis;
using RateLimiter.UnitTests.Fixtures;
using RateLimiter.UnitTests.TestDoubles;
using Xunit;

namespace RateLimiter.UnitTests.Redis;

/// Sequential, single-threaded correctness tests: "does the sliding window
/// algorithm behave correctly at all?" - independent of concurrency. The
/// race-condition tests in RedisRateLimiterRaceConditionTests.cs build on
/// top of this baseline by adding concurrency.

[Collection("Redis collection")]
public sealed class RedisRateLimiterTests
{
    private readonly RedisFixture _fixture;

    public RedisRateLimiterTests(RedisFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CheckAsync_AllowsRequestsUpToTheLimitThenBlocks()
    {
        // Arrange - a fresh, unique client ID per test avoids any
        // cross-test interference within the one shared Redis container.
        var clientId = $"client-{Guid.NewGuid():N}";
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 3, windowSeconds: 60);
        var limiter = new RedisRateLimiterRepository(_fixture.Connection, policyStore);

        // Act
        var first = await limiter.CheckAsync(clientId);
        var second = await limiter.CheckAsync(clientId);
        var third = await limiter.CheckAsync(clientId);
        var fourth = await limiter.CheckAsync(clientId);

        // Assert - exactly the first 3 succeed, the 4th is blocked.
        first.IsAllowed.Should().BeTrue();
        second.IsAllowed.Should().BeTrue();
        third.IsAllowed.Should().BeTrue();
        fourth.IsAllowed.Should().BeFalse();
        fourth.RetryAfterSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CheckAsync_RemainingCountDecreasesWithEachAllowedRequest()
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 5, windowSeconds: 60);
        var limiter = new RedisRateLimiterRepository(_fixture.Connection, policyStore);

        var first = await limiter.CheckAsync(clientId);
        var second = await limiter.CheckAsync(clientId);

        first.Remaining.Should().Be(4);
        second.Remaining.Should().Be(3);
    }

    [Fact]
    public async Task CheckAsync_TreatsDifferentClientsCompletelyIndependently()
    {
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 1, windowSeconds: 60);
        var limiter = new RedisRateLimiterRepository(_fixture.Connection, policyStore);

        var clientA = $"client-a-{Guid.NewGuid():N}";
        var clientB = $"client-b-{Guid.NewGuid():N}";

        var aFirst = await limiter.CheckAsync(clientA);
        var aSecond = await limiter.CheckAsync(clientA); // should now be blocked - A's budget is used up
        var bFirst = await limiter.CheckAsync(clientB);  // separate client, separate budget entirely

        aFirst.IsAllowed.Should().BeTrue();
        aSecond.IsAllowed.Should().BeFalse();
        bFirst.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_AllowsRequestsAgainAfterTheWindowSlidesPast()
    {
        // A deliberately short window (1 second) so this test proves the
        // window actually "slides" without needing to sleep for a full
        // minute to do it.
        var clientId = $"client-{Guid.NewGuid():N}";
        var policyStore = new FixedClientPolicyStore(limitPerWindow: 1, windowSeconds: 1);
        var limiter = new RedisRateLimiterRepository(_fixture.Connection, policyStore);

        var first = await limiter.CheckAsync(clientId);
        var immediatelyAfter = await limiter.CheckAsync(clientId);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var afterWindowExpires = await limiter.CheckAsync(clientId);

        first.IsAllowed.Should().BeTrue();
        immediatelyAfter.IsAllowed.Should().BeFalse();
        afterWindowExpires.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_ReturnsUnknownClientResult_WhenTheClientIsNotRegistered()
    {
        // This never even reaches Redis for the counting logic - the
        // policy store returning null short-circuits straight to an
        // UnknownClient result, which the HTTP layer turns into a 404.
        var policyStore = new NullClientPolicyStore();
        var limiter = new RedisRateLimiterRepository(_fixture.Connection, policyStore);

        var result = await limiter.CheckAsync("this-client-was-never-registered");

        result.IsUnknownClient.Should().BeTrue();
        result.IsAllowed.Should().BeFalse();
    }
}