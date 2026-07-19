using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;

namespace RateLimiter.UnitTests.TestDoubles;

/// An IClientPolicyStore that always returns null, simulating "this client
/// is not registered" for every lookup - used to test the unknown-client
/// rejection path in RedisRateLimiter and LocalMemoryRateLimiter without
/// needing a real Postgres-backed store.
public sealed class NullClientPolicyStore : IClientPolicyStoreRepository
{
    public Task<ClientPolicy?> GetPolicyAsync(string clientId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<ClientPolicy?>(null);
    }
}