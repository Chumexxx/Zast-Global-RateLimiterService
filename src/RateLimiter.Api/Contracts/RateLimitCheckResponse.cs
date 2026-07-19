namespace RateLimiter.Api.Contracts;

/// <param name="ClientId">The client that was checked.</param>
/// <param name="Allowed">Whether this request may proceed.</param>
/// <param name="Limit">The client's configured limit for the current window.</param>
/// <param name="Remaining">Requests left in the current window (0 if blocked).</param>
/// <param name="RetryAfterSeconds">Seconds to wait before retrying (0 if allowed).</param>
/// <param name="IsFailSafe">

public sealed record RateLimitCheckResponse(
    string ClientId,
    bool Allowed,
    int Limit,
    long Remaining,
    int RetryAfterSeconds,
    bool IsFailSafe);
