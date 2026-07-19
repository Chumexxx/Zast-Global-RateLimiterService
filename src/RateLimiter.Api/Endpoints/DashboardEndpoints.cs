using RateLimiter.Api.Contracts;
using RateLimiter.Domain.Services;

namespace RateLimiter.Api.Endpoints;

public static class DashboardEndpoints
{
    private const int MinPeriodDays = 1;
    private const int MaxPeriodDays = 366;

    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/dashboard").WithTags("Dashboard");

        group.MapGet("/clients", GetClientsAsync)
            .WithName("GetDashboardClients")
            .WithSummary("All clients in the system.")
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK);

        group.MapGet("/{clientId}/summary", GetSummaryAsync)
            .WithName("GetDashboardSummary")
            .WithSummary("Usage totals, allowed/blocked breakdown, average response time, and a daily trend for a client")
            .Produces<DashboardSummaryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetClientsAsync(IDashboardQueryService dashboardQueryService, CancellationToken cancellationToken)
    {
        var clients = await dashboardQueryService.GetKnownClientIdsAsync(cancellationToken);
        return Results.Ok(clients);
    }

    private static async Task<IResult> GetSummaryAsync(string clientId, int days, IDashboardQueryService dashboardQueryService, CancellationToken cancellationToken)
    {
        if (days < MinPeriodDays || days > MaxPeriodDays)
        {
            return Results.BadRequest(new
            {
                error = $"'days' must be between {MinPeriodDays} and {MaxPeriodDays}."
            });
        }

        var summary = await dashboardQueryService.GetSummaryAsync(clientId, days, cancellationToken);

        if (summary is null)
        {
            return Results.NotFound(new
            {
                error = $"Unknown client '{clientId}'. This client is not registered in client_policies."
            });
        }

        var response = new DashboardSummaryResponse(
            ClientId: summary.ClientId,
            PeriodDays: summary.PeriodDays,
            TotalRequests: summary.TotalRequests,
            AllowedRequests: summary.AllowedRequests,
            BlockedRequests: summary.BlockedRequests,
            AverageResponseTimeMs: summary.AverageResponseTimeMs,
            DailyTrend: summary.DailyTrend
                .Select(p => new DailyUsagePointResponse(
                    p.Date, p.TotalRequests, p.AllowedRequests, p.BlockedRequests, p.AverageResponseTimeMs))
                .ToList());

        return Results.Ok(response);
    }
}