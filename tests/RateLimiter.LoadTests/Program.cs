using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace RateLimiter.LoadTests;

/// Zast Global Rate Limiter — Load & Performance Tests.
///
/// This is a runnable console app (`dotnet run`), NOT part of `dotnet test` -
/// load tests are a deliberate, manual action you run against a live,
/// already-running cluster (`docker compose up --build` first), not
/// something that should fire automatically on every build.
///
/// WHAT EACH SCENARIO PROVES:
///   1. SustainedLoadSingleClient - proves the rate limiter stays fast even under sustained heavy
///      concurrent traffic against one hot client.
///   2. BurstAgainstLowLimitClient - proves that REJECTING requests (429s) is just as fast as allowing them - a naive implementation might
///      accidentally do extra, slower work on the "blocked" path.
///   3. MultiTenantMixedLoad - proves the system holds up when many different clients are hitting it concurrently, simulating the
///      brief's description of "hundreds of external APIs" being checked at once, not just one.
///
public static class Program
{
    public static void Main(string[] args)
    {
        var targetBaseUrl = Environment.GetEnvironmentVariable("TARGET_BASE_URL") ?? "http://localhost:8080";

        Console.WriteLine($"Load testing target: {targetBaseUrl}");
        Console.WriteLine("Make sure `docker compose up --build` is already running before starting this test.");

        using var httpClient = new HttpClient { BaseAddress = new Uri(targetBaseUrl) };

        // ====================================================================
        // Scenario 1: sustained load against a single, high-limit client
        // ====================================================================
        // Client B is configured for 5000 requests/minute (see db/init.sql),
        // deliberately high enough that this scenario mostly exercises the
        // "allowed" path rather than immediately exhausting the budget and
        // only testing the "blocked" path - we want a clean read on Redis
        // round-trip latency under real concurrent load.
        var sustainedLoadScenario = Scenario.Create("sustained_load_single_client", async context =>
        {
            using var httpResponse = await httpClient.PostAsync("/api/v1/ratelimit/client-b/check", null);
            return await BuildResponseAsync(httpResponse);
        })
        .WithoutWarmUp() // we want every request's latency counted, including the very first
        .WithLoadSimulations(
            // A realistic traffic SHAPE - ramp up, hold at a steady peak,
            // ramp down - rather than an instant on/off spike. This is what
            // makes the percentile numbers reflect genuine steady-state
            // behavior instead of just the chaos of an initial burst.
            Simulation.RampingInject(rate: 300, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(10)),
            Simulation.Inject(rate: 300, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30)),
            Simulation.RampingInject(rate: 0, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5))
        );

        // ====================================================================
        // Scenario 2: sustained burst against a LOW-limit client
        // ====================================================================
        // Client C is configured for only 20 requests/minute - this scenario
        // fires far more traffic than that limit allows, guaranteeing the
        // vast majority of requests get a 429. The point: rejecting a
        // request still requires running the full Lua script
        // (ZREMRANGEBYSCORE + ZCARD), so a naive implementation could be
        // slower on this path, not faster. This scenario proves that isn't
        // the case here.
        var burstScenario = Scenario.Create("burst_against_low_limit_client", async context =>
        {
            using var httpResponse = await httpClient.PostAsync("/api/v1/ratelimit/client-c/check", null);
            return await BuildResponseAsync(httpResponse);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.Inject(rate: 150, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20))
        );

        // ====================================================================
        // Scenario 3: many different clients concurrently (multi-tenant load)
        // ====================================================================
        // Simulates the brief's actual production scenario more literally:
        // "hundreds of external APIs" being checked, not just one hot key.
        var clientIds = new[] { "client-a", "client-b", "client-c" };

        var multiTenantScenario = Scenario.Create("multi_tenant_mixed_load", async context =>
        {
            var clientId = clientIds[context.InvocationNumber % clientIds.Length];
            using var httpResponse = await httpClient.PostAsync($"/api/v1/ratelimit/{clientId}/check", null);
            return await BuildResponseAsync(httpResponse);
        })
        .WithoutWarmUp()
        .WithLoadSimulations(
            Simulation.RampingInject(rate: 400, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
        );

        NBomberRunner
            .RegisterScenarios(sustainedLoadScenario, burstScenario, multiTenantScenario)
            .WithReportFolder("reports")
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv, ReportFormat.Md)
            .Run();

        Console.WriteLine();
        Console.WriteLine("Load test complete. Open reports/*.html for full latency percentile charts and throughput graphs.");
        Console.WriteLine("What to look for (see README.md's 'Verifying the load test' section for the full checklist):");
        Console.WriteLine("  - sustained_load_single_client: p95 and p99 latency should stay in single-digit-to-low-double-digit milliseconds.");
        Console.WriteLine("  - burst_against_low_limit_client: latency should be comparable to Scenario 1, NOT slower on the rejection path.");
        Console.WriteLine("  - All scenarios: error rate (excluding expected 429s) should be at or near 0%.");
    }

    /// Converts a raw HttpResponseMessage into an NBomber Response, with the
    /// classification that matters for this project: a 200 OR a 429 both
    /// count as a SUCCESSFUL outcome for these scenarios, because a 429
    /// means the rate limiter correctly rejected an over-quota request -
    /// that's the system working as designed, not failing.
    ///
    /// WHY THIS MATTERS (this was a real bug in an earlier draft): if you
    /// unconditionally return Response.Ok(...) for every status code
    /// without this check, NBomber can never detect a genuine failure - a
    /// 500, a connection refused, a timeout would ALL be reported as "Ok",
    /// silently defeating the entire purpose of tracking an error rate.
    /// Only 200 and 429 are treated as Ok here; everything else (5xx, or
    /// any other unexpected status) is reported as a Fail so it actually
    /// shows up in the report.
    private static async Task<Response<object>> BuildResponseAsync(HttpResponseMessage httpResponse)
    {
        var statusCode = ((int)httpResponse.StatusCode).ToString();
        var bytes = httpResponse.Content is null
            ? []
            : await httpResponse.Content.ReadAsByteArrayAsync();

        var isExpectedOutcome = httpResponse.IsSuccessStatusCode || (int)httpResponse.StatusCode == 429;

        return isExpectedOutcome
            ? Response.Ok(statusCode: statusCode, sizeBytes: bytes.Length)
            : Response.Fail<object>(statusCode: statusCode, message: $"Unexpected status code: {statusCode}");
    }
}
