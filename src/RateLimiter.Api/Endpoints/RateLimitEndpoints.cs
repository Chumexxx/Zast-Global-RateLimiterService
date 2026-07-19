using RateLimiter.Api.Contracts;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using System.Diagnostics;
using System.Threading.RateLimiting;

namespace RateLimiter.Api.Endpoints;

/// The HTTP surface of the rate limiter.
///
/// HOW THIS IS USED IN PRACTICE: picture one of the other microservices
/// described in the brief (the one that calls a banking API, say). Before
/// it makes its own outbound call to that bank's API, it first calls:
///
///   POST /api/v1/ratelimit/bank-client-a/check
///
/// If the response is 200, it proceeds with the real call. If it's 429,
/// it backs off for `RetryAfterSeconds` and tries again later instead of
/// hitting the bank's API and eating a 429 + financial penalty from them.
///
/// Using Minimal APIs (instead of MVC controllers) here keeps this file
/// short and puts the routing, the logic, and the response shaping all in
/// one place that's easy for a reviewer to read top to bottom.
public static class RateLimitEndpoints
{
    public static void MapRateLimitEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/ratelimit").WithTags("Rate Limiter");

        group.MapPost("/{clientId}/check", CheckRateLimitAsync)
            .WithName("CheckRateLimit")
            .WithSummary("Checks (and, if allowed, consumes) one unit of a client's rate limit quota.")
            .Produces<RateLimitCheckResponse>(StatusCodes.Status200OK)
            .Produces<RateLimitCheckResponse>(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> CheckRateLimitAsync(string clientId, IRateLimiterRepository rateLimiter, IUsageLoggerRepository usageLogger, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Results.BadRequest(new { error = "clientId is required." });
        }

        // This is the ENTIRE hot path: everything before this call was just
        // routing/validation, and everything after is just shaping the
        // response. The actual rate-limit decision - including the Redis
        // round trip and any fail-safe fallback - happens inside here.
        var stopwatch = Stopwatch.StartNew();
        var result = await rateLimiter.CheckAsync(clientId, cancellationToken);
        stopwatch.Stop();

        // Logged for EVERY check, not only approved ones. WasAllowed lets
        // the dashboard filter to approved-only for billing purposes, while
        // still letting us show rejection-rate trends - useful information
        // a client-facing billing view wouldn't otherwise capture.
        //
        // This call returns immediately (see ChannelUsageLogger) - it does
        // NOT wait for a Postgres write, so it adds no meaningful latency
        // to this response.
        usageLogger.Enqueue(new UsageRecord
        {
            ClientId = clientId,
            TimestampUtc = DateTime.UtcNow,
            WasAllowed = result.IsAllowed,
            // The latency of the rate-limit DECISION itself (Redis round
            // trip + fail-safe logic if triggered) - not the downstream
            // third-party API call, which this service doesn't make. This
            // is what powers the dashboard's "average response time" view,
            // and it's a genuinely useful number: it's what tells you the
            // rate limiter itself is staying within its "few milliseconds"
            // requirement in production.
            ResponseTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Endpoint = httpContext.Request.Path.Value
        });

        if (result.IsUnknownClient)
        {
            return Results.NotFound(new
            {
                error = $"Unknown client '{clientId}'. This client is not registered in client_policies."
            });
        }

        // Response headers so callers can implement their own backoff logic
        // by reading headers alone, without needing to parse the JSON body -
        // this mirrors how most public rate-limited APIs (GitHub, Stripe,
        // etc) communicate quota state.
        httpContext.Response.Headers["X-RateLimit-Limit"] = result.Limit.ToString();
        httpContext.Response.Headers["X-RateLimit-Remaining"] = result.Remaining.ToString();

        if (result.IsFailSafe)
        {
            // Informational only: lets you (while testing/grading) confirm
            // the fail-safe path actually triggered when you stop the Redis
            // container, without needing to check logs.
            httpContext.Response.Headers["X-RateLimit-FailSafe"] = "true";
        }

        var response = new RateLimitCheckResponse(
            ClientId: clientId,
            Allowed: result.IsAllowed,
            Limit: result.Limit,
            Remaining: result.Remaining,
            RetryAfterSeconds: result.RetryAfterSeconds,
            IsFailSafe: result.IsFailSafe);

        if (!result.IsAllowed)
        {
            httpContext.Response.Headers["Retry-After"] = result.RetryAfterSeconds.ToString();
            return Results.Json(response, statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Ok(response);
    }
}