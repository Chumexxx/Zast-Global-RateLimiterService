using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RateLimiter.Domain.Models;
using RateLimiter.Infrastructure.Persistence;
using RateLimiter.Infrastructure.Persistence.Entities;

namespace RateLimiter.Infrastructure.Logging;

/// Drains <see cref="ChannelUsageLogger"/>'s channel and writes records to
/// Postgres in batches. Runs for the lifetime of the application as a
/// hosted <see cref="BackgroundService"/>.
///
/// TWO INDEPENDENT LOOPS RUN CONCURRENTLY, FOR TWO DIFFERENT REASONS:
///   1. <see cref="DrainChannelLoopAsync"/> reacts to new records arriving
///      and flushes as soon as a batch fills up - this keeps Postgres
///      write volume sane under HIGH load (one INSERT per ~200 requests,
///      not one per request).
///   2. <see cref="PeriodicFlushLoopAsync"/> flushes on a fixed timer
///      regardless of batch size - this matters under LOW load, where a
///      batch of 200 might never fill up on its own, and without this,
///      a handful of log records could sit unwritten (and invisible to
///      the dashboard) indefinitely.
/// Both loops share one in-memory list, protected by a SemaphoreSlim
/// (async-friendly locking - a plain `lock` statement can't be held across
/// an `await`, which we need here).
/// </summary>
public sealed class UsageLogWorker : BackgroundService
{
    private const int MaxBatchSize = 200;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly Channel<UsageRecord> _channel;
    private readonly IDbContextFactory<RateLimiterDbContext> _dbContextFactory;
    private readonly ILogger<UsageLogWorker> _logger;

    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly List<UsageRecord> _pendingBatch = new(MaxBatchSize);

    public UsageLogWorker(
        Channel<UsageRecord> channel,
        IDbContextFactory<RateLimiterDbContext> dbContextFactory,
        ILogger<UsageLogWorker> logger)
    {
        _channel = channel;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drainTask = DrainChannelLoopAsync(stoppingToken);
        var timerTask = PeriodicFlushLoopAsync(stoppingToken);

        await Task.WhenAll(drainTask, timerTask);

        // Both loops have stopped (graceful shutdown) - flush anything
        // still sitting in memory rather than silently losing it.
        await FlushPendingBatchAsync(CancellationToken.None);
    }

    private async Task DrainChannelLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var record in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                bool shouldFlush;

                await _batchLock.WaitAsync(stoppingToken);
                try
                {
                    _pendingBatch.Add(record);
                    shouldFlush = _pendingBatch.Count >= MaxBatchSize;
                }
                finally
                {
                    _batchLock.Release();
                }

                if (shouldFlush)
                {
                    await FlushPendingBatchAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown - nothing to do here, the
            // final flush in ExecuteAsync handles anything left over.
        }
    }

    private async Task PeriodicFlushLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushPendingBatchAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
    }

    private async Task FlushPendingBatchAsync(CancellationToken cancellationToken)
    {
        List<UsageRecord> toWrite;

        // Always allowed to acquire the lock, even mid-shutdown (note
        // CancellationToken.None here) - we want the final flush to
        // actually happen, not be cancelled away.
        await _batchLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_pendingBatch.Count == 0)
            {
                return;
            }

            toWrite = new List<UsageRecord>(_pendingBatch);
            _pendingBatch.Clear();
        }
        finally
        {
            _batchLock.Release();
        }

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

            var entities = toWrite.Select(r => new UsageLogEntity
            {
                ClientId = r.ClientId,
                TimestampUtc = r.TimestampUtc,
                WasAllowed = r.WasAllowed,
                ResponseTimeMs = r.ResponseTimeMs,
                Endpoint = r.Endpoint
            });

            db.UsageLogs.AddRange(entities);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // If Postgres is down, we log and move on rather than crash
            // this background service. Throwing here would stop ALL future
            // logging until the app restarts - far worse than losing one
            // batch of analytics rows. The rate limiter's hot path is
            // completely unaffected either way, by design.
            _logger.LogError(ex,
                "Failed to flush {Count} usage log records to Postgres. These records are lost.",
                toWrite.Count);
        }
    }
}
