namespace RateLimiter.Infrastructure.Persistence.Entities;

/// EF Core entity mapping to the `usage_logs` table (see db/init.sql).
///
/// This is deliberately a SEPARATE class from RateLimiter.Domain.Models.UsageRecord,
/// even though they look nearly identical. That's intentional: Domain.Models
/// represents "what a usage record means to our business logic", while this
/// class represents "what a row in this specific Postgres table looks like."
/// Keeping them separate means a future database schema change (renaming a
/// column, splitting a table) never forces a change to Domain - only to
/// this mapping and the small bit of code that translates between the two.
public sealed class UsageLogEntity
{
    public long Id { get; set; }
    public string ClientId { get; set; } = default!;
    public DateTime TimestampUtc { get; set; }
    public bool WasAllowed { get; set; }
    public double? ResponseTimeMs { get; set; }
    public string? Endpoint { get; set; }
}
