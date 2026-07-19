using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using RateLimiter.Infrastructure.FailSafe;
using StackExchange.Redis;
using Xunit;

namespace RateLimiter.UnitTests.FailSafe;

/// Tests FailSafeRateLimiter's DECISION LOGIC in isolation, using mocked
/// IRateLimiter dependencies rather than a real Redis instance. This is
/// deliberate: we want to simulate specific failure modes (a dead
/// connection, a timeout, a hang) precisely and deterministically, which
/// is much harder to do reliably against a real Redis container (you'd
/// have to actually kill/pause the container mid-test, which is slow and
/// flaky). The real-Redis race-condition tests already prove the
/// underlying Redis behavior is correct; these tests prove the fail-safe
/// WRAPPER around it behaves correctly.
public sealed class FailSafeRateLimiterTests
{
    [Fact]
    public async Task CheckAsync_WhenPrimarySucceeds_ReturnsPrimaryResultAndNeverTouchesFallback()
    {
        var expected = RateLimitResult.Allowed(remaining: 9, limit: 10);

        var primary = new Mock<IRateLimiterRepository>();
        primary
            .Setup(p => p.CheckAsync("client-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var fallback = new Mock<IRateLimiterRepository>();

        var limiter = new FailSafeRateLimiter(primary.Object, fallback.Object, Mock.Of<ILogger<FailSafeRateLimiter>>());

        var result = await limiter.CheckAsync("client-a");

        result.Should().BeSameAs(expected);
        fallback.Verify(f => f.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "the fallback should never be touched at all when Redis is healthy - not even to warm it up");
    }

    [Theory]
    [MemberData(nameof(RedisFailureExceptions))]
    public async Task CheckAsync_WhenPrimaryThrowsARedisError_FallsBackToTheLocalLimiter(Exception thrownException)
    {
        var primary = new Mock<IRateLimiterRepository>();
        primary
            .Setup(p => p.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrownException);

        var fallbackResult = RateLimitResult.Allowed(remaining: 4, limit: 5, isFailSafe: true);
        var fallback = new Mock<IRateLimiterRepository>();
        fallback
            .Setup(f => f.CheckAsync("client-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackResult);

        var limiter = new FailSafeRateLimiter(primary.Object, fallback.Object, Mock.Of<ILogger<FailSafeRateLimiter>>());

        var result = await limiter.CheckAsync("client-a");

        result.Should().BeSameAs(fallbackResult);
        fallback.Verify(f => f.CheckAsync("client-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    public static IEnumerable<object[]> RedisFailureExceptions()
    {
        // The two concrete exception types StackExchange.Redis throws for "the connection is down" and
        // "the connection is too slow" - exactly the scenarios the brief's fail-safe requirement is about.
        yield return new object[]
        {
            new RedisConnectionException(ConnectionFailureType.UnableToConnect, "simulated connection failure")
        };
        yield return new object[]
        {
            new RedisTimeoutException("simulated timeout", CommandStatus.Unknown)
        };
    }

    [Fact]
    public async Task CheckAsync_WhenPrimaryHangsPastItsTimeoutBudget_FallsBackWithoutWaitingForIt()
    {
        var primary = new Mock<IRateLimiterRepository>();
        primary
            .Setup(p => p.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // Deliberately much longer than FailSafeRateLimiter's
                // internal 75ms timeout budget - simulating a Redis
                // instance that's alive but unresponsive, rather than
                // fully disconnected. This is the harder case to handle
                // correctly: a dead connection fails fast and obviously,
                // but a HANGING one requires an explicit timeout to avoid
                // stalling the caller indefinitely.
                await Task.Delay(TimeSpan.FromSeconds(5));
                return RateLimitResult.Allowed(1, 10);
            });

        var fallbackResult = RateLimitResult.Allowed(remaining: 9, limit: 10, isFailSafe: true);
        var fallback = new Mock<IRateLimiterRepository>();
        fallback
            .Setup(f => f.CheckAsync("client-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackResult);

        var limiter = new FailSafeRateLimiter(primary.Object, fallback.Object, Mock.Of<ILogger<FailSafeRateLimiter>>());

        var stopwatch = Stopwatch.StartNew();
        var result = await limiter.CheckAsync("client-a");
        stopwatch.Stop();

        result.Should().BeSameAs(fallbackResult);

        // Should return well before the primary's simulated 5-second delay
        // would have completed - proving the timeout budget actually cuts
        // the wait short instead of being purely decorative.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "the fail-safe timeout should make this return almost immediately, not after " +
            "waiting for the (simulated) hanging Redis call to eventually finish");
    }

    [Fact]
    public async Task CheckAsync_WhenCallerCancelsTheRequestThemselves_PropagatesTheCancellationInsteadOfFallingBack()
    {
        // Important distinction: if the CALLER cancels (e.g. the HTTP
        // request itself was aborted), that is not a Redis failure and
        // should not be silently converted into a fallback response - it
        // should propagate as a normal cancellation.
        using var callerCts = new CancellationTokenSource();

        var primary = new Mock<IRateLimiterRepository>();
        primary
            .Setup(p => p.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken ct) =>
            {
                callerCts.Cancel(); // simulate the caller cancelling mid-flight
                ct.ThrowIfCancellationRequested();
                return RateLimitResult.Allowed(1, 10);
            });

        var fallback = new Mock<IRateLimiterRepository>();

        var limiter = new FailSafeRateLimiter(primary.Object, fallback.Object, Mock.Of<ILogger<FailSafeRateLimiter>>());

        var act = async () => await limiter.CheckAsync("client-a", callerCts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fallback.Verify(f => f.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a caller-initiated cancellation should propagate directly, never be masked as a Redis failure");
    }
}
