# Zast Global Rate Limiter as a Service

This is a high-availability, cluster-safe rate limiter built for the Vega IT Abuja Tech Challenge qualification task. It protects outbound calls to third-party APIs (banking, logistics, AI providers) with hard per-client quotas that hold correctly no matter which instance in the cluster handles a given request.

![Architecture Diagram](architecture-diagram.png)

---

## Table of contents

1. [What this solves](#what-this-solves)
2. [Architecture at a glance](#architecture-at-a-glance)
3. [Project structure](#project-structure)
4. [Prerequisites](#prerequisites)
5. [Running it — Docker Compose (the real deliverable)](#running-it--docker-compose-the-real-deliverable)
6. [Running it — local dev loop](#running-it--local-dev-loop)
7. [Trying the API](#trying-the-api)
8. [The dashboard](#the-dashboard)
9. [Running the tests](#running-the-tests)
10. [Verifying edge cases](#verifying-edge-cases)
11. [Key design decisions](#key-design-decisions)
12. [Known limitations and trade-offs](#known-limitations-and-trade-offs)

---

## What this solves

Every microservice in the company currently manages its own rate limits internally. Run 10 instances of one service, and each instance thinks it has the *entire* quota to itself — 10x over-consumption, `429`s from third parties, and financial penalties.

This service centralizes that decision: any microservice calls **this** service first ("can I make one more request to Client A's API right now?") before making its own outbound call. The requirements this had to satisfy:

- Different limits per client (Client A: 100/min, Client B: 5000/min, ...)
- Correct behavior across multiple instances of this service running at once
- A "few milliseconds" decision latency
- Must not block all traffic if Redis or Postgres becomes unavailable
- Every request logged for analytics/billing, without slowing down the decision itself
- A dashboard with usage trends over 10/15/30-day windows

## Architecture at a glance

```
Client / other microservices
        │
        ▼
     Nginx (:8080, least_conn load balancing)
        │
   ┌────┼────┐
   ▼    ▼    ▼
 api-1 api-2 api-3          <- 3 identical instances, proving cluster-safety
   │    │    │
   └────┼────┘
        ▼
     Redis                  <- shared counter, atomic Lua script (the hot path)
        │
        ▼ (only on Redis failure/timeout)
  Local in-memory fallback  <- fail-safe path, per-instance

   (separately, async, never blocking the above)
        │
        ▼
  Channel<UsageRecord> → UsageLogWorker → Postgres (usage_logs)
                                              │
                                              ▼
                                    Dashboard read API + frontend
```

See `architecture-diagram.jpg` for the full annotated version.

## Project structure

```
RateLimiterService/
├── docker-compose.yml          Redis, Postgres, 3 API instances, Nginx
├── db/init.sql                 Postgres schema + seeded client policies
├── nginx/nginx.conf            Load balancer config
├── diagram/architecture-diagram.png/.jpg
├── src/
│   ├── RateLimiter.Domain/          Interfaces, Models & Services, zero dependencies
│   ├── RateLimiter.Infrastructure/  Redis, Postgres/EF Core, fail-safe logic
│   └── RateLimiter.Api/             Minimal API endpoints, Program.cs, wwwroot dashboard
└── tests/
    ├── RateLimiter.UnitTests/       xUnit + Testcontainers (real Redis) + Moq
    └── RateLimiter.LoadTests/       NBomber load/performance scenarios
```

This follows Clean/Onion Architecture: `Api` depends on `Infrastructure` depends on `Domain`, never the reverse. `Domain` has no knowledge that Redis or Postgres even exist — it only defines contracts (`IRateLimiter`, `IClientPolicyStore`, `IUsageLogger`, `IDashboardQueryService`).

## Prerequisites

- **Docker Desktop** (includes Docker Compose) — required either way
- **.NET 8 SDK** — required for the local dev loop and for running tests/load tests from your machine

```bash
docker --version
docker compose version
dotnet --version   # should print 8.0.x (a later 9.x SDK can still build/run a net8.0 project fine)
```

## Running it — Docker Compose

From the repo root (where `docker-compose.yml` lives):

```bash
docker compose up --build -d
```

The `-d` (detached) flag matters — without it, closing or Ctrl+C-ing that terminal stops every container immediately.

First run takes a few minutes (pulling base images + compiling 3 API images). Subsequent runs are much faster thanks to Docker layer caching.

Confirm everything came up:
```bash
docker compose ps
```
You should see 6 containers: `redis`, `postgres`, `nginx`, `api-1`, `api-2`, `api-3`, all `Up`.

Stop everything:
```bash
docker compose down        # stop + remove containers
docker compose down -v     # also wipe Redis/Postgres data volumes for a clean slate
```

## Running it — local dev loop

Faster iteration when working on code, at the cost of not exercising the real 3-instance cluster or Nginx:

```bash
docker compose up redis postgres -d
dotnet restore
dotnet build
dotnet run --project src/RateLimiter.Api
```

This uses `appsettings.json`'s `localhost` connection strings, which work because Compose still publishes Redis/Postgres to your host's `6379`/`5432` ports.

## Trying the API

All examples assume the full Docker Compose stack is running (port 8080). On Windows PowerShell, use `curl.exe` explicitly — plain `curl` is aliased to `Invoke-WebRequest`, which doesn't understand `-X`/`-i` flags the same way.

**Check a client's quota:**
```bash
curl.exe -i -X POST http://localhost:8080/api/v1/ratelimit/client-a/check
```
Response headers to look at: `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-Served-By` (which api-1/2/3 instance answered), and — only if Redis was unreachable — `X-RateLimit-FailSafe: true`.

**Swagger UI** (easier for exploring interactively):
```
http://localhost:8080/swagger
```

**Seeded clients** (see `db/init.sql`):
client_id  | limit | window  
-----------|-------|---------
client-a   |   100 |  60s    
client-b   |  5000 |  60s    
client-c   |    20 |  60s    
client-d   |     5 |  60s    
client-e   |     1 |  60s    
client-f   |    50 |  10s    
client-g   |  1000 |  60s    
client-h   | 10000 |  60s    
client-i   |   200 | 300s    
client-j   |    30 |   5s    
client-k   |     2 |   1s    

## The dashboard

```
http://localhost:8080/
```

Pick a client from the dropdown, choose a random day window bwtween 1 and 366, and see total requests, allowed/blocked breakdown, average response time, and daily trend charts. Data only appears here a couple of seconds after you make requests — the logging pipeline batches writes every 2 seconds or 200 records, whichever comes first (see `UsageLogWorker`).

## Running the tests

**Unit tests** (includes the race-condition tests — see below). Docker Desktop must be running, since these use Testcontainers to spin up a real, throwaway Redis instance — but you do **not** need `docker compose up` running first, the tests manage their own container:
```bash
dotnet test tests/RateLimiter.UnitTests
```

Run just the race-condition tests:
```bash
dotnet test tests/RateLimiter.UnitTests --filter "FullyQualifiedName~RaceCondition"
```

**Load tests** — these *do* need the full stack already running (`docker compose up --build -d` first):
```bash
dotnet run --project tests/RateLimiter.LoadTests
```
Reports land in `tests/RateLimiter.LoadTests/reports/*.html` (also `.csv` and `.md`).

## Verifying edge cases

### 1. Per-client limits are enforced correctly
```bash
for /L %i in (1,1,22) do curl.exe -s -o NUL -w "%i -> %{http_code}\n" -X POST http://localhost:8080/api/v1/ratelimit/client-c/check
```
(PowerShell equivalent:)
```powershell
for ($i = 1; $i -le 22; $i++) {
    $code = curl.exe -s -o NUL -w "%{http_code}" -X POST http://localhost:8080/api/v1/ratelimit/client-c/check
    Write-Host "$i -> $code"
}
```
Client C's limit is 20/min — requests 1–20 should return `200`, 21+ should return `429` with a `Retry-After` header.

### 2. Cluster-safety under real concurrency
```powershell
$testClientId = "concurrency-demo"   # any client ID not already partially used this minute
$results = 1..50 | ForEach-Object -Parallel {
    curl.exe -s -o NUL -w "%{http_code}" -X POST "http://localhost:8080/api/v1/ratelimit/$using:testClientId/check"
} -ThrottleLimit 50
$results | Group-Object | Select-Object Name, Count
```
Requires PowerShell 7+ for `-Parallel` (`$PSVersionTable.PSVersion` to check; `winget install --id Microsoft.PowerShell` if needed). Unregistered client IDs fall back to the default policy (10/min), so expect exactly 10× `200` and 40× `429` — never more than 10 allowed, even though the 50 requests land across all 3 api instances simultaneously.

The same proof, automated and repeatable, lives in `RedisRateLimiterRaceConditionTests.cs` — that's the test to point to as definitive evidence, since it also verifies the scenario with genuinely separate `RedisRateLimiter` instances (simulating separate processes) rather than relying on Nginx's real load balancing.

### 3. Fail-safe behavior when Redis goes down
```bash
docker compose stop redis
curl.exe -i -X POST http://localhost:8080/api/v1/ratelimit/client-a/check
```
Should still return `200`/`429` (never a `500` or a hang), now with `X-RateLimit-FailSafe: true`. Bring Redis back and confirm the header disappears:
```bash
docker compose start redis
curl.exe -i -X POST http://localhost:8080/api/v1/ratelimit/client-a/check
```

### 4. Postgres unavailability doesn't break the rate limiter
```bash
docker compose stop postgres
curl.exe -i -X POST http://localhost:8080/api/v1/ratelimit/client-a/check
```
Should still work — logging silently drops records (see `ChannelUsageLogger`'s warning logs) and the policy cache keeps serving its last known values (see `PostgresClientPolicyStore.RefreshAsync`'s catch block) rather than failing the request.

### 5. Changing a client's limit without a redeploy
```bash
docker compose exec postgres psql -U ratelimiter -d ratelimiter -c "UPDATE client_policies SET limit_per_window = 50 WHERE client_id = 'client-c';"
```
Within 30 seconds (the policy cache refresh interval), `client-c`'s effective limit changes across all 3 instances with no restart.

## Key design decisions

A few choices worth understanding, not just accepting:

- **Sliding window log (Redis sorted set) over a fixed-window counter** — avoids the classic boundary-burst bug where a fixed window lets 2x the limit through across a window edge.
- **Redis's own clock (`TIME` command) inside the Lua script**, not a C#-side timestamp — removes clock drift between `api-1/2/3` as a source of bugs.
- **The entire check runs as one atomic Lua script** — this is what actually prevents the race condition; two requests hitting two different instances at the same instant are still serialized by Redis.
- **Fail-safe is a deliberate, documented trade-off, not a bug** — during a Redis outage, each instance protects itself independently, meaning the *effective* limit across 3 instances could briefly be ~3x the configured one. This is intentional: the brief explicitly requires availability over perfect accuracy during an outage.
- **Logging is fully decoupled from the hot path** — an in-memory `Channel` plus a background batching worker, so the "log every request" requirement never adds latency to the "stay fast" requirement.
- **The policy store and dashboard queries never touch Postgres on the hot path** — both are cached/pre-aggregated so the actual rate-limit check only ever talks to Redis.

## Known limitations and trade-offs

- The fail-safe fallback is per-instance, not coordinated — see the design decision above and the doc comment on `LocalMemoryRateLimiter` for the full reasoning.
- The load tests need to be run manually against a live stack and interpreted by a human reading the generated report; there's no automated pass/fail gate on specific latency numbers, since acceptable latency varies by machine.
- `client_policies` and `usage_logs` don't have any retention/archival policy — in a real production system, old `usage_logs` rows would need a cleanup job over time.
