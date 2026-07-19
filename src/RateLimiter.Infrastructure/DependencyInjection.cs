using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;
using RateLimiter.Domain.Services;
using RateLimiter.Infrastructure.Dashboard;
using RateLimiter.Infrastructure.FailSafe;
using RateLimiter.Infrastructure.Logging;
using RateLimiter.Infrastructure.Persistence;
using RateLimiter.Infrastructure.Policies;
using RateLimiter.Infrastructure.Redis;
using StackExchange.Redis;

namespace RateLimiter.Infrastructure;

/// Single entry point for wiring up every Infrastructure service. The Api
/// project calls AddRateLimiterInfrastructure(...) once at startup and
/// doesn't need to know any of the details below - this is what keeps
/// Program.cs short and readable.
public static class DependencyInjection
{
    public static IServiceCollection AddRateLimiterInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetConnectionString("Redis")
            ?? throw new InvalidOperationException(
                "Missing 'ConnectionStrings:Redis' configuration. Check appsettings.json or the " +
                "ConnectionStrings__Redis environment variable set in docker-compose.yml.");

        var postgresConnectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "Missing 'ConnectionStrings:Postgres' configuration. Check appsettings.json or the " +
                "ConnectionStrings__Postgres environment variable set in docker-compose.yml.");

        // ====================================================================
        // Redis
        // ====================================================================
        // A single ConnectionMultiplexer is shared for the whole app's
        // lifetime (registered as a singleton) - this is StackExchange.Redis's
        // documented best practice. Creating a new one per request would be
        // slow and would defeat connection pooling/multiplexing entirely.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);

            // Don't crash app startup if Redis isn't reachable the instant
            // we boot (e.g. container start-order during `docker compose up`
            // before Redis's healthcheck has passed) - keep retrying in the
            // background instead. Combined with FailSafeRateLimiter, this
            // means the app comes up and serves traffic (via the fallback)
            // even if Redis is a few seconds late.
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            options.SyncTimeout = 1000;

            return ConnectionMultiplexer.Connect(options);
        });

        // ====================================================================
        // Postgres (EF Core)
        // ====================================================================
        // AddPooledDbContextFactory (rather than plain AddDbContext) is the
        // recommended pattern for background services: BackgroundService
        // instances are effectively singletons, but a DbContext is NOT
        // thread-safe and isn't meant to be long-lived. The factory lets
        // both the HTTP pipeline and our background workers create a
        // short-lived DbContext exactly when they need one, drawing from a
        // shared, pooled connection pool underneath.
        services.AddPooledDbContextFactory<RateLimiterDbContext>(options =>
            options.UseNpgsql(postgresConnectionString));

        // ====================================================================
        // Client policy store - Postgres-backed, cached in memory
        // ====================================================================
        // Registered as both its concrete type (so PolicyCacheRefreshWorker
        // can call its non-interface RefreshAsync method) and as the
        // IClientPolicyStore interface (so everything else in the app only
        // ever depends on the abstraction).
        services.AddSingleton<PostgresClientPolicyStore>();
        services.AddSingleton<IClientPolicyStoreRepository>(sp => sp.GetRequiredService<PostgresClientPolicyStore>());
        services.AddHostedService<PolicyCacheRefreshWorker>();

        // ====================================================================
        // Rate limiter chain (Redis -> fail-safe -> local fallback)
        // ====================================================================
        // Keyed DI (a .NET 8 feature) lets us register two different
        // IRateLimiter implementations side by side and have
        // FailSafeRateLimiter ask for exactly the ones it needs by name.
        services.AddKeyedSingleton<IRateLimiterRepository, RedisRateLimiterRepository>("primary");
        services.AddKeyedSingleton<IRateLimiterRepository, LocalMemoryRateLimiter>("fallback");

        // This is the IRateLimiter that gets resolved everywhere else in
        // the app. Callers have no idea Redis or a fallback are even
        // involved - they just see "can this client proceed?"
        services.AddSingleton<IRateLimiterRepository, FailSafeRateLimiter>();

        // ====================================================================
        // Async usage logging (Channel -> BackgroundService -> Postgres)
        // ====================================================================
        // Bounded, not unbounded: an unbounded channel could grow without
        // limit and exhaust memory if Postgres falls behind for an extended
        // period. Bounding it forces an explicit, documented decision about
        // what happens when it fills up (see ChannelUsageLogger's comments)
        // instead of silently consuming all available RAM.
        services.AddSingleton(Channel.CreateBounded<UsageRecord>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false (rather than blocking) when full
            SingleReader = true,                    // only UsageLogWorker reads from this channel
            SingleWriter = false                    // many concurrent HTTP requests write to it
        }));

        services.AddSingleton<IUsageLoggerRepository, ChannelUsageLogger>();
        services.AddHostedService<UsageLogWorker>();

        // ====================================================================
        // Dashboard read side
        // ====================================================================
        // Stateless aside from the shared DbContextFactory, so a singleton
        // is fine here - same reasoning as PostgresClientPolicyStore.
        services.AddSingleton<IDashboardQueryService, DashboardQueryService>();

        return services;
    }
}
