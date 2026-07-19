using Microsoft.EntityFrameworkCore;
using RateLimiter.Infrastructure.Persistence.Entities;

namespace RateLimiter.Infrastructure.Persistence;

/// The EF Core gateway to Postgres. Deliberately thin - all it does is
/// describe the shape of the two tables we care about (usage_logs and
/// client_policies) and how they map to our entity classes.
///
/// WHY EXPLICIT COLUMN MAPPING INSTEAD OF A NAMING-CONVENTION PACKAGE:
/// Our Postgres tables use snake_case (client_id, timestamp_utc), while
/// C# convention is PascalCase (ClientId, TimestampUtc). Rather than add
/// another NuGet package to auto-translate between the two, we map each
/// column explicitly below - a few extra lines, but zero "magic," and
/// anyone reading this file can see exactly which C# property maps to
/// which SQL column without needing to know about a separate convention
/// package's behavior.
public sealed class RateLimiterDbContext : DbContext
{
    public RateLimiterDbContext(DbContextOptions<RateLimiterDbContext> options) : base(options)
    {
    }

    public DbSet<UsageLogEntity> UsageLogs => Set<UsageLogEntity>();
    public DbSet<ClientPolicyEntity> ClientPolicies => Set<ClientPolicyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageLogEntity>(entity =>
        {
            entity.ToTable("usage_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ClientId).HasColumnName("client_id").HasMaxLength(100);
            entity.Property(e => e.TimestampUtc).HasColumnName("timestamp_utc");
            entity.Property(e => e.WasAllowed).HasColumnName("was_allowed");
            entity.Property(e => e.ResponseTimeMs).HasColumnName("response_time_ms");
            entity.Property(e => e.Endpoint).HasColumnName("endpoint").HasMaxLength(200);

            // Mirrors the index created in db/init.sql - declaring it here
            // too keeps EF's model in sync with reality and means
            // `dotnet ef migrations` incase I add migrations later
            entity.HasIndex(e => new { e.ClientId, e.TimestampUtc });
        });

        modelBuilder.Entity<ClientPolicyEntity>(entity =>
        {
            entity.ToTable("client_policies");
            entity.HasKey(e => e.ClientId);
            entity.Property(e => e.ClientId).HasColumnName("client_id").HasMaxLength(100);
            entity.Property(e => e.LimitPerWindow).HasColumnName("limit_per_window");
            entity.Property(e => e.WindowSeconds).HasColumnName("window_seconds");
        });
    }
}
