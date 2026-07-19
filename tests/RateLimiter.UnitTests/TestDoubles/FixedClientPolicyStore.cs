using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;

namespace RateLimiter.UnitTests.TestDoubles;

/// A minimal, fixed-policy IClientPolicyStore for tests that need a
/// predictable limit/window without needing Postgres or the overhead of
/// mocking framework setup for something this trivial.
public sealed class FixedClientPolicyStore : IClientPolicyStoreRepository
{
    private readonly int _limitPerWindow;
    private readonly int _windowSeconds;

    public FixedClientPolicyStore(int limitPerWindow, int windowSeconds)
    {
        _limitPerWindow = limitPerWindow;
        _windowSeconds = windowSeconds;
    }

    public Task<ClientPolicy?> GetPolicyAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var policy = new ClientPolicy
        {
            ClientId = clientId,
            LimitPerWindow = _limitPerWindow,
            WindowSeconds = _windowSeconds
        };
        return Task.FromResult<ClientPolicy?>(policy);
    }
}