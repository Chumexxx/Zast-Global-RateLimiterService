using FluentAssertions;
using RateLimiter.Infrastructure.Policies;
using Xunit;

namespace RateLimiter.UnitTests.Policies;

public sealed class InMemoryClientPolicyStoreTests
{
    [Theory]
    [InlineData("client-a", 100)]
    [InlineData("client-b", 5000)]
    [InlineData("client-c", 20)]
    public async Task GetPolicyAsync_ReturnsTheSeededLimitForClients(string clientId, int expectedLimit)
    {
        var store = new InMemoryClientPolicyStore();

        var policy = await store.GetPolicyAsync(clientId);

        policy.LimitPerWindow.Should().Be(expectedLimit);
        policy.WindowSeconds.Should().Be(60);
    }

    [Fact]
    public async Task GetPolicyAsync_ReturnsNullForUnknownClients()
    {
        var store = new InMemoryClientPolicyStore();

        var policy = await store.GetPolicyAsync("some-client-that-was-never-registered");

        policy.Should().BeNull();
    }
}