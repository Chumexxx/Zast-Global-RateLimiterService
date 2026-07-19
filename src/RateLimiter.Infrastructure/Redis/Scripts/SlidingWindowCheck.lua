-- ============================================================================
-- Sliding Window Rate Limiter — Atomic "Check And Increment"
-- ============================================================================
-- Algorithm: Sliding Window Log, implemented with a Redis Sorted Set (ZSET).
--   - Each member of the set is one request; its score is the timestamp
--     (in milliseconds) at which it happened.
--   - "How many requests in the last N seconds?" becomes "how many members
--     have a score within [now - N*1000, now]?" which ZSETs answer cheaply.
--
-- KEYS[1] = the Redis key for this client, e.g. "ratelimit:client-a"
-- ARGV[1] = window size in milliseconds (e.g. 60000 for "per minute")
-- ARGV[2] = limit (max requests allowed inside the window)
-- ARGV[3] = a unique id for this request (prevents ZADD collisions if two
--           requests land in the exact same Redis-server millisecond)
-- ARGV[4] = key TTL in seconds (pure housekeeping — lets Redis garbage
--           collect a client's key automatically if they go idle)
--
-- Returns a 2-element array: { allowed, secondValue }
--   - allowed = 1 and secondValue = requests remaining, OR
--   - allowed = 0 and secondValue = milliseconds until retry is worth trying
--
-- ----------------------------------------------------------------------------
-- WHY THIS IS SAFE ACROSS MULTIPLE APP INSTANCES (api-1, api-2, api-3):
-- Redis runs an entire Lua script as ONE atomic, uninterruptible operation.
-- No other command from any client — on any api instance — can be
-- interleaved between the ZREMRANGEBYSCORE / ZCARD / ZADD calls below.
-- This is what actually prevents the race condition described in the
-- assessment brief: two requests hitting api-1 and api-2 at literally the
-- same instant cannot both read "1 slot remaining" and both get allowed
-- through. Redis serializes all Lua script executions, full stop.
--
-- WHY WE ASK REDIS FOR THE TIME (redis.call('TIME')) INSTEAD OF PASSING A
-- TIMESTAMP FROM THE C# SIDE:
-- api-1/2/3 are separate containers/machines and their system clocks can
-- drift relative to each other, even if only by milliseconds. If each
-- instance used its own clock, "now" would mean something slightly
-- different depending on which instance handled the request, undermining
-- the sliding window's accuracy. Using Redis's own clock makes Redis the
-- single, consistent source of truth for "now" — clock drift on the app
-- servers becomes a non-issue.
-- ============================================================================

local key = KEYS[1]
local windowMs = tonumber(ARGV[1])
local limit = tonumber(ARGV[2])
local member = ARGV[3]
local ttlSeconds = tonumber(ARGV[4])

local time = redis.call('TIME')
local nowMs = (tonumber(time[1]) * 1000) + math.floor(tonumber(time[2]) / 1000)
local windowStart = nowMs - windowMs

-- Step 1: evict everything older than the window. This is what keeps the
-- sorted set bounded in size — it never grows past roughly `limit` entries
-- for a well-behaved client, so ZCARD below stays cheap.
redis.call('ZREMRANGEBYSCORE', key, '-inf', windowStart)

-- Step 2: how many requests remain inside the window after eviction?
local count = redis.call('ZCARD', key)

if count < limit then
    -- Step 3a: allowed. Record this request and refresh the key's TTL.
    redis.call('ZADD', key, nowMs, member)
    redis.call('EXPIRE', key, ttlSeconds)
    return { 1, limit - count - 1 }
else
    -- Step 3b: blocked. Look at the oldest request in the window to tell
    -- the caller roughly when a slot will free up, so they can back off
    -- intelligently instead of retrying immediately in a hot loop.
    local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
    local oldestScore = tonumber(oldest[2])
    local retryAfterMs = windowMs - (nowMs - oldestScore)
    if retryAfterMs < 0 then
        retryAfterMs = 0
    end
    return { 0, retryAfterMs }
end
