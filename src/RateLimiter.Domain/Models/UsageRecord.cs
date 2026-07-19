namespace RateLimiter.Domain.Models;

/// One row of "this client made a request, and here's what happened."
/// This is what eventually lands in Postgres and powers the dashboard
/// (trend graphs, average response time, etc).
///
/// IMPORTANT DESIGN NOTE:
/// We do NOT write this to Postgres synchronously during the rate-limit
/// check. That would violate the "must remain ultra-fast" requirement.
/// Instead, the API pushes these records into an in-memory channel (a
/// thread-safe queue), and a background worker batches them into Postgres.
/// See: RateLimiter.Infrastructure/Logging/UsageLogWorker.cs
public sealed class UsageRecord
{
    public required string ClientId { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required bool WasAllowed { get; init; }
    public double? ResponseTimeMs { get; init; }
    public string? Endpoint { get; init; }
}
