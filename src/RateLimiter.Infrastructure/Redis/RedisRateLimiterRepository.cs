using System.Reflection;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using StackExchange.Redis;

namespace RateLimiter.Infrastructure.Redis;

/// The primary, accurate rate limiter. Talks directly to Redis and runs the
/// atomic sliding-window Lua script (Redis/Scripts/SlidingWindowCheck.lua)
/// to decide whether a client may proceed.
///
/// DESIGN NOTE — why this class does NOT try/catch Redis exceptions:
/// If Redis is unreachable or too slow, this class simply throws. It is
/// deliberately "unaware" of fail-safe behavior. That responsibility lives
/// one layer up, in <see cref="FailSafe.FailSafeRateLimiter"/>. This keeps
/// each class doing exactly one job:
///   - RedisRateLimiter: "talk to Redis correctly and accurately"
///   - FailSafeRateLimiter: "decide what to do when that's not possible"

public sealed class RedisRateLimiterRepository : IRateLimiterRepository
{
    private const string KeyPrefix = "ratelimit:";

    // The Lua script's text, read once from the embedded resource and
    // reused for every call - there's no reason to re-read it per request.
    private static readonly string LuaScript = LoadEmbeddedScript();

    private readonly IConnectionMultiplexer _redis;
    private readonly IClientPolicyStoreRepository _policyStore;

    public RedisRateLimiterRepository(IConnectionMultiplexer redis, IClientPolicyStoreRepository policyStore)
    {
        _redis = redis;
        _policyStore = policyStore;
    }

    public async Task<RateLimitResult> CheckAsync(string clientId, CancellationToken cancellationToken = default)
    {
        // Look up this client's limit (e.g. Client A: 100/min).
        // This lookup is currently in-memory so it doesn't add its own latency to the hot path.
        var policy = await _policyStore.GetPolicyAsync(clientId, cancellationToken);

        if (policy is null)
        {
            // Client isn't registered at all - reject explicitly.
            return RateLimitResult.UnknownClient();
        }

        var db = _redis.GetDatabase();
        var key = KeyPrefix + clientId;

        var member = Guid.NewGuid().ToString("N");

        var windowMs = policy.WindowSeconds * 1000;

        var ttlSeconds = policy.WindowSeconds + 5;

        var raw = await db.ScriptEvaluateAsync(
            LuaScript,
            keys: new RedisKey[] { key },
            values: new RedisValue[] { windowMs, policy.LimitPerWindow, member, ttlSeconds });

        // The script always returns a 2-element array: [allowed, secondValue]
        var result = (RedisResult[])raw!;
        var allowed = (long)result[0] == 1;
        var secondValue = (long)result[1];

        if (allowed)
        {
            return RateLimitResult.Allowed(remaining: secondValue, limit: policy.LimitPerWindow);
        }

        // secondValue is milliseconds-until-retry here; the HTTP-facing
        // Retry-After convention is in whole seconds, so we round up -
        // better to tell a client to wait slightly too long than too short.
        var retryAfterSeconds = (int)Math.Ceiling(secondValue / 1000.0);
        return RateLimitResult.Blocked(limit: policy.LimitPerWindow, retryAfterSeconds: retryAfterSeconds);
    }

    private static string LoadEmbeddedScript()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Search for any embedded resource whose name ENDS WITH the script's
        // filename, rather than requiring an exact, hardcoded match. This
        // survives folder renames, namespace changes, or the file moving -
        // and if it ever fails again, the exception below prints every
        // embedded resource name the assembly actually has, turning a silent
        // mismatch into an obvious diagnostic instead of a guessing game.
        const string scriptFileName = "SlidingWindowCheck.lua";

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(scriptFileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            var availableResources = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"Could not find an embedded resource ending in '{scriptFileName}'. " +
                $"Resources actually embedded in this assembly: [{availableResources}]. " +
                "Check the <EmbeddedResource> entry in RateLimiter.Infrastructure.csproj " +
                "matches the .lua file's actual current path.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
