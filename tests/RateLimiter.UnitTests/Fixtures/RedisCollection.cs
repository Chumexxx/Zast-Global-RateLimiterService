using Xunit;

namespace RateLimiter.UnitTests.Fixtures;

/// Ties test classes together so they share ONE Redis container instead of
/// each spinning up its own (which would work, but make the test suite
/// much slower - starting a Docker container per test class adds up fast).
/// Apply with [Collection("Redis collection")] on any test class that
/// needs RedisFixture injected into its constructor.
[CollectionDefinition("Redis collection")]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    // Intentionally empty - this class exists only to carry the
    // [CollectionDefinition] attribute and the ICollectionFixture<> marker.
}
