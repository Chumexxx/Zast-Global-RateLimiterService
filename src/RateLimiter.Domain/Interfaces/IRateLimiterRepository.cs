using RateLimiter.Domain.Models;

namespace RateLimiter.Domain.Interfaces;

///"can this client make a request right now?"

public interface IRateLimiterRepository
{
    /// Attempts to consume one unit of quota for the given client.
    Task<RateLimitResult> CheckAsync(string clientId, CancellationToken cancellationToken = default);
}
