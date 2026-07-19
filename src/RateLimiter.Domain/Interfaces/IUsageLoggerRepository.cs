using RateLimiter.Domain.Models;

namespace RateLimiter.Domain.Interfaces;

/// Records that a request happened, for later analytics/billing/dashboard use.

public interface IUsageLoggerRepository
{
    /// Enqueues a usage record. This should return almost instantly - the
    /// actual write to persistent storage happens asynchronously in the
    /// background (see UsageLogWorker).
    void Enqueue(UsageRecord record);
}
