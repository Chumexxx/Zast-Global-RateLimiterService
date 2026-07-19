using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using RateLimiter.Api.Endpoints;
using RateLimiter.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Services
// ============================================================================

// One line pulls in everything from Step 3: IConnectionMultiplexer (Redis),
// IClientPolicyStore, and the IRateLimiter chain (Redis -> fail-safe ->
// local fallback). Program.cs doesn't need to know any of those details -
// that's the point of the DependencyInjection.cs extension method.
builder.Services.AddRateLimiterInfrastructure(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "Zast Global Rate Limiter as a Service",
        Version = "v1",
        Description = "HA rate limiter for third-party API quotas."
    });
});

// Two separate health check tiers, for two separate purposes:
//
// - Liveness ("is the process alive at all?") should NOT depend on Redis
//   or Postgres - if it did, an orchestrator might kill and restart a
//   perfectly healthy API instance just because Redis had a brief hiccup,
//   which is the exact opposite of the fail-safe behavior we built.
//
// - Readiness ("should traffic be routed to this instance?") DOES check
//   Redis and Postgres, so a load balancer can temporarily stop sending
//   traffic to an instance that's degraded, without killing it outright.
builder.Services.AddHealthChecks()
    .AddRedis(
        builder.Configuration.GetConnectionString("Redis")!,
        name: "redis",
        tags: ["ready"])
    .AddNpgSql(
        builder.Configuration.GetConnectionString("Postgres")!,
        name: "postgres",
        tags: ["ready"]);

var app = builder.Build();

// ============================================================================
// Middleware pipeline
// ============================================================================

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Rate Limiter API v1");
});

// Serves wwwroot/index.html (the dashboard frontend) at "/" and any other
// static assets it references. UseDefaultFiles must run before
// UseStaticFiles so that a request for "/" resolves to "/index.html"
// instead of returning a 404.
app.UseDefaultFiles();
app.UseStaticFiles();

// Stamps every response with which api-1/2/3 instance actually handled it.
// This has no functional purpose - it exists so that while you're testing
// through Nginx (localhost:8080), you can visually confirm requests are
// really being load-balanced across all 3 instances, which is central to
// proving the "cluster-safe" requirement.
var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? Environment.MachineName;
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Served-By"] = instanceName;
        return Task.CompletedTask;
    });
    await next();
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    // No checks actually run for this endpoint - Predicate always false
    // means "just confirm the process can respond at all."
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapRateLimitEndpoints();
app.MapDashboardEndpoints();

app.Run();

// Exposes the auto-generated Program class to the test project (WebApplicationFactory
// needs this for integration-style tests, since top-level statements otherwise
// generate an internal, inaccessible Program class).
public partial class Program { }
