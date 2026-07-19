namespace RateLimiter.Infrastructure.Persistence.Entities;

/// EF Core entity mapping to the `client_policies` table (see db/init.sql).
/// This is what lets support staff change a client's rate limit with a
/// simple SQL UPDATE, instead of a code change and redeploy.
public sealed class ClientPolicyEntity
{
    public string ClientId { get; set; } = default!;
    public int LimitPerWindow { get; set; }
    public int WindowSeconds { get; set; }
}
