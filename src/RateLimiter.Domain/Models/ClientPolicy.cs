namespace RateLimiter.Domain.Models;

/// Defines how many requests a specific client is allowed to make, and over what time window. 

/// In a real system these would be loaded from Postgres and cached, so that
/// support staff can change a client's limit without redeploying code.
/// For this assessment we seed a small set of clients (see PolicyStore).
public sealed class ClientPolicy
{
    public required string ClientId { get; init; }
    public required int LimitPerWindow { get; init; }
    public required int WindowSeconds { get; init; }
}
