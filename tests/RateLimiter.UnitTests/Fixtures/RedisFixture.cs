using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace RateLimiter.UnitTests.Fixtures;

/// Starts a REAL, throwaway Redis container (via Testcontainers) once per
/// test collection, and tears it down when the collection finishes.
///
/// WHY A REAL REDIS INSTANCE INSTEAD OF A MOCK OR FAKE:
/// The whole point of the race-condition tests in this project is to
/// prove that Redis's Lua-script atomicity actually prevents multiple
/// concurrent requests from over-consuming a client's quota. A mocked
/// IConnectionMultiplexer can't prove that - it would just prove that our
/// C# code calls the mock the way we expect. Only a real Redis engine can
/// genuinely exercise (and validate) the atomicity guarantee the whole
/// design depends on.
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer _container = null!;

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new RedisBuilder()
            .WithImage("redis:7.2-alpine")
            .Build();

        await _container.StartAsync();

        Connection = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}
