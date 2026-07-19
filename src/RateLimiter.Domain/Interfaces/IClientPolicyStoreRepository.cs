using RateLimiter.Domain.Models;

namespace RateLimiter.Domain.Interfaces;

/// Looks up the rate limit policy (limit + window) for a given client.
/// Kept separate from IRateLimiter so that "how do we know the limit"
/// and "how do we enforce the limit" are independent concerns - you could
/// swap a hardcoded/in-memory store for a Postgres-backed one without
/// touching the rate limiting algorithm at all.
public interface IClientPolicyStoreRepository
{
    /// Returns the policy for a client, or null if the client is not registered.
    Task<ClientPolicy?> GetPolicyAsync(string clientId, CancellationToken cancellationToken = default);
}
