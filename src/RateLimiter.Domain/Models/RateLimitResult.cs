namespace RateLimiter.Domain.Models;


/// The result of asking "can this client make a request right now?"
/// 
/// We return more than just true/false because:
///  - Clients need to know how many requests they have left (Remaining).
///  - Clients need to know when they can try again (RetryAfter), so they can
///    back off intelligently instead of hammering us again immediately.
///  - We need to know if the decision came from a degraded/fail-safe path
///    (IsFailSafe), purely for observability/logging - it does NOT change
///    how the caller treats the request, but it helps us monitor how often
///    Redis is unavailable in production.

public sealed class RateLimitResult
{
    public required bool IsAllowed { get; init; }
    public required long Remaining { get; init; }
    public required int Limit { get; init; }
    public required int RetryAfterSeconds { get; init; }
    public bool IsFailSafe { get; init; }
    public bool IsUnknownClient { get; init; }

    public static RateLimitResult Allowed(long remaining, int limit, bool isFailSafe = false) => new()
    {
        IsAllowed = true,
        Remaining = remaining,
        Limit = limit,
        RetryAfterSeconds = 0,
        IsFailSafe = isFailSafe
    };

    public static RateLimitResult Blocked(int limit, int retryAfterSeconds) => new()
    {
        IsAllowed = false,
        Remaining = 0,
        Limit = limit,
        RetryAfterSeconds = retryAfterSeconds
    };

    public static RateLimitResult UnknownClient() => new()
    {
        IsAllowed = false,
        Remaining = 0,
        Limit = 0,
        RetryAfterSeconds = 0,
        IsUnknownClient = true
    };
}
