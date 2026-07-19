namespace RateLimiter.Domain.Models;

/// A daily aggregate of usage for one client - one bar/point on the dashboard's trend chart.
public sealed class DailyUsagePoint
{
    public required DateOnly Date { get; init; }
    public required long TotalRequests { get; init; }
    public required long AllowedRequests { get; init; }
    public required long BlockedRequests { get; init; }
    public required double AverageResponseTimeMs { get; init; }
}

/// The full answer to "how has this client been using the system over the last N days?" - the shape the dashboard's summary view is built from.
public sealed class UsageSummary
{
    public required string ClientId { get; init; }
    public required int PeriodDays { get; init; }
    public required long TotalRequests { get; init; }
    public required long AllowedRequests { get; init; }
    public required long BlockedRequests { get; init; }

    public required double AverageResponseTimeMs { get; init; }
    public required IReadOnlyList<DailyUsagePoint> DailyTrend { get; init; }
}
