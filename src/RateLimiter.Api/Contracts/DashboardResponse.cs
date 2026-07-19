namespace RateLimiter.Api.Contracts;

public sealed record DailyUsagePointResponse(
    DateOnly Date,
    long TotalRequests,
    long AllowedRequests,
    long BlockedRequests,
    double AverageResponseTimeMs);

public sealed record DashboardSummaryResponse(
    string ClientId,
    int PeriodDays,
    long TotalRequests,
    long AllowedRequests,
    long BlockedRequests,
    double AverageResponseTimeMs,
    IReadOnlyList<DailyUsagePointResponse> DailyTrend);


