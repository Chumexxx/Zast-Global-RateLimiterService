using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;

namespace RateLimiter.Infrastructure.Policies;

/// <summary>
/// A simple, hardcoded lookup of client -> rate limit policy, seeded to
/// match.
///
/// WHY THIS IS FINE FOR NOW, AND WHAT CHANGES LATER:
///
/// Crucially, nothing outside this class needs to know or care about that
/// change: RedisRateLimiter and LocalMemoryRateLimiter only depend on the
/// IClientPolicyStore interface, so swapping this implementation out is a
/// one-line change in DependencyInjection.cs.
/// </summary>
public sealed class InMemoryClientPolicyStore : IClientPolicyStoreRepository
{
    private static readonly Dictionary<string, ClientPolicy> Policies = new()
    {
        ["client-a"] = new ClientPolicy { ClientId = "client-a", LimitPerWindow = 100, WindowSeconds = 60 },
        ["client-b"] = new ClientPolicy { ClientId = "client-b", LimitPerWindow = 5000, WindowSeconds = 60 },
        ["client-c"] = new ClientPolicy { ClientId = "client-c", LimitPerWindow = 20, WindowSeconds = 60 },
    };

    // Applied to any client ID we don't recognize. A conservative default
    // (rather than throwing, or worse, allowing unlimited requests) means
    // a typo'd or unregistered client ID fails safely too.
    private const int DefaultLimitPerWindow = 10;
    private const int DefaultWindowSeconds = 60;

    public Task<ClientPolicy?> GetPolicyAsync(string clientId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Policies.GetValueOrDefault(clientId));
    }
}
