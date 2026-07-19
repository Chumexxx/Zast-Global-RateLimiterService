using Microsoft.EntityFrameworkCore;
using RateLimiter.Domain.Models;
using RateLimiter.Domain.Services;
using RateLimiter.Infrastructure.Persistence;

namespace RateLimiter.Infrastructure.Dashboard;


/// Answers dashboard queries by reading the usage_logs table that UsageLogWorker 
///
/// This class deliberately does the aggregation (theGROUP BY) IN POSTGRES via EF Core's translated LINQ, not by pulling raw
/// rows into C# memory and grouping them there. Once usage_logs has millions of rows, "load everything then group in memory" would be both
/// slow and memory-hungry; letting Postgres's query planner do the aggregation is the only approach that scales.
public sealed class DashboardQueryService : IDashboardQueryService
{
    private const int MinPeriodDays = 1;
    private const int MaxPeriodDays = 366;

    private readonly IDbContextFactory<RateLimiterDbContext> _dbContextFactory;

    public DashboardQueryService(IDbContextFactory<RateLimiterDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<UsageSummary?> GetSummaryAsync(
        string clientId,
        int periodDays,
        CancellationToken cancellationToken = default)
    {
        if (periodDays < MinPeriodDays || periodDays > MaxPeriodDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodDays),
                periodDays,
                $"periodDays must be between {MinPeriodDays} and {MaxPeriodDays}.");
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var clientExists = await db.ClientPolicies
            .AsNoTracking()
            .AnyAsync(p => p.ClientId == clientId, cancellationToken);

        if (!clientExists)
        {
            return null;
        }

        var periodStartUtc = DateTime.UtcNow.Date.AddDays(-(periodDays - 1));

        // This whole query - the filter, the GROUP BY, and the three aggregate functions - is translated by EF Core into a single SQL
        // statement. Nothing here pulls raw rows into .NET memory.
        var dailyGroups = await db.UsageLogs
            .AsNoTracking()
            .Where(l => l.ClientId == clientId && l.TimestampUtc >= periodStartUtc)
            .GroupBy(l => l.TimestampUtc.Date)
            .Select(g => new DailyUsagePoint
            {
                Date = DateOnly.FromDateTime(g.Key),
                TotalRequests = g.LongCount(),
                AllowedRequests = g.LongCount(l => l.WasAllowed),
                BlockedRequests = g.LongCount(l => !l.WasAllowed),
                AverageResponseTimeMs = g.Average(l => l.ResponseTimeMs ?? 0)
            })
            .OrderBy(p => p.Date)
            .ToListAsync(cancellationToken);

        // Postgres only returns rows for days that actually had traffic.
        // We fill in the gaps so the trend chart shows an honest "0 requests" for a quiet day instead of a misleading gap that a
        // charting library might otherwise interpolate across.
        var filledTrend = FillMissingDays(dailyGroups, periodStartUtc, periodDays);

        var totalRequests = filledTrend.Sum(p => p.TotalRequests);
        var allowedRequests = filledTrend.Sum(p => p.AllowedRequests);
        var blockedRequests = filledTrend.Sum(p => p.BlockedRequests);

        // Traffic-weighted average - see the XML doc on UsageSummary for
        // why we don't just average the per-day averages.
        var weightedMsTotal = filledTrend.Sum(p => p.AverageResponseTimeMs * p.TotalRequests);
        var averageResponseTimeMs = totalRequests > 0 ? weightedMsTotal / totalRequests : 0d;

        return new UsageSummary
        {
            ClientId = clientId,
            PeriodDays = periodDays,
            TotalRequests = totalRequests,
            AllowedRequests = allowedRequests,
            BlockedRequests = blockedRequests,
            AverageResponseTimeMs = averageResponseTimeMs,
            DailyTrend = filledTrend
        };
    }

    public async Task<IReadOnlyList<string>> GetKnownClientIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.ClientPolicies
            .AsNoTracking()
            .OrderBy(p => p.ClientId)
            .Select(p => p.ClientId)
            .ToListAsync(cancellationToken);
    }

    private static List<DailyUsagePoint> FillMissingDays(List<DailyUsagePoint> existingPoints, DateTime periodStartUtc, int periodDays)
    {
        var byDate = existingPoints.ToDictionary(p => p.Date);
        var result = new List<DailyUsagePoint>(periodDays);

        for (var i = 0; i < periodDays; i++)
        {
            var date = DateOnly.FromDateTime(periodStartUtc.AddDays(i));

            result.Add(byDate.TryGetValue(date, out var existingPoint)
                ? existingPoint
                : new DailyUsagePoint
                {
                    Date = date,
                    TotalRequests = 0,
                    AllowedRequests = 0,
                    BlockedRequests = 0,
                    AverageResponseTimeMs = 0
                });
        }

        return result;
    }
}