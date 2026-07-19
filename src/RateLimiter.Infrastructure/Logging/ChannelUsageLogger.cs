using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RateLimiter.Domain.Interfaces;
using RateLimiter.Domain.Models;

namespace RateLimiter.Infrastructure.Logging;

/// The IUsageLogger implementation actually used by the app. Writes go
/// into an in-memory <see cref="Channel{T}"/> (think: a thread-safe queue
/// built for exactly this producer/consumer scenario) and return
/// immediately. A separate background service (<see cref="UsageLogWorker"/>)
/// drains the channel and writes to Postgres in batches, completely
/// decoupled from the HTTP request that triggered the log.

public sealed class ChannelUsageLogger : IUsageLoggerRepository
{
    private readonly Channel<UsageRecord> _channel;
    private readonly ILogger<ChannelUsageLogger> _logger;

    public ChannelUsageLogger(Channel<UsageRecord> channel, ILogger<ChannelUsageLogger> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    public void Enqueue(UsageRecord record)
    {
        // TryWrite is synchronous and non-blocking: it either succeeds
        // immediately, or fails immediately if the channel is full. We
        // deliberately never `await` here - doing so (e.g. via WriteAsync)
        // could make a caller wait if the channel were momentarily full,
        // which would defeat the entire point of this class.
        var wasWritten = _channel.Writer.TryWrite(record);

        if (!wasWritten)
        {
            // The channel is at capacity, meaning the background worker
            // can't keep up with the incoming rate (or Postgres itself is
            // struggling). We drop the log entry rather than block the
            // request or throw an exception up into the hot path.
            //
            // TRADE-OFF, STATED EXPLICITLY: losing a handful of analytics
            // rows during an extreme traffic spike is an acceptable cost.
            // Blocking (or failing) the actual rate-limit check because
            // the *logging* pipeline is backed up would not be - logging
            // is a side effect of a request, never a prerequisite for it.
            _logger.LogWarning(
                "Usage log channel is full; dropping a log entry for client {ClientId}. " +
                "This means the background Postgres writer is falling behind - check Postgres health.",
                record.ClientId);
        }
    }
}
