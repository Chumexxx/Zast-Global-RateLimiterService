using RateLimiter.Domain.Models;

namespace RateLimiter.Domain.Services;

/// The read side of the system - everything the dashboard needs. Kept
/// completely separate from IRateLimiter/IUsageLogger (the write/hot-path
/// side) so that dashboard query complexity can never leak into, or slow
/// down, the actual rate-limiting logic. This mirrors the CQRS
/// (Command Query Responsibility Segregation) idea: writes and reads are
/// different concerns with very different performance requirements, so we
/// let them evolve independently.
public interface IDashboardQueryService
{
    /// Builds a usage summary (totals + daily trend) for a client over a specific period of days. Returns null if the client
    /// isn't registered in client_policies at all 
    Task<UsageSummary?> GetSummaryAsync(string clientId, int periodDays, CancellationToken cancellationToken = default);
 
    /// Returns all client IDs 
    Task<IReadOnlyList<string>> GetKnownClientIdsAsync(CancellationToken cancellationToken = default);
}
 
