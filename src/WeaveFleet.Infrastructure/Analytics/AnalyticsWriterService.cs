using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// Hosted background service that drains the analytics channel in batches and writes
/// to the analytics database. Also calls <see cref="ISessionRepository.IncrementTokensAsync"/>
/// to update main DB session token totals, and records OTEL metrics.
/// Follows the <c>HarnessEventRelay</c> pattern for scoped dependencies and shutdown handling.
/// </summary>
public sealed partial class AnalyticsWriterService : BackgroundService
{
    private const int ProcessedEventIdCapacity = 100_000;

    private readonly AnalyticsCollector _collector;
    private readonly IAnalyticsDbConnectionFactory _analyticsDb;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FleetOptions _options;
    private readonly ILogger<AnalyticsWriterService> _logger;

    /// <summary>
    /// Tracks event IDs that have already triggered a main DB increment.
    /// Bounded to <see cref="ProcessedEventIdCapacity"/> entries to prevent unbounded growth.
    /// </summary>
    private readonly HashSet<string> _processedEventIds = new(ProcessedEventIdCapacity);

    public AnalyticsWriterService(
        AnalyticsCollector collector,
        IAnalyticsDbConnectionFactory analyticsDb,
        IServiceScopeFactory scopeFactory,
        FleetOptions options,
        ILogger<AnalyticsWriterService> logger)
    {
        _collector = collector;
        _analyticsDb = analyticsDb;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flushInterval = TimeSpan.FromSeconds(_options.AnalyticsFlushIntervalSeconds);
        var maxBatch = _options.AnalyticsMaxBatchSize;

        LogStarted(flushInterval, maxBatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            var batch = new List<AnalyticsEventEnvelope>(maxBatch);

            try
            {
                // Wait for first item or until flush interval expires
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                flushCts.CancelAfter(flushInterval);

                try
                {
                    await foreach (var item in _collector.Reader.ReadAllAsync(flushCts.Token))
                    {
                        batch.Add(item);
                        if (batch.Count >= maxBatch)
                            break;
                    }
                }
                catch (OperationCanceledException) when (flushCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Flush interval expired — write whatever we have
                }
            }
            catch (OperationCanceledException)
            {
                // App is shutting down — drain remaining items without a deadline
                await DrainRemainingAsync(batch, stoppingToken);
            }

            if (batch.Count > 0)
                await FlushBatchAsync(batch);
        }
    }

    private async Task DrainRemainingAsync(List<AnalyticsEventEnvelope> batch, CancellationToken stoppingToken)
    {
        while (_collector.Reader.TryRead(out var item))
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
            await FlushBatchAsync(batch);
    }

    private async Task FlushBatchAsync(List<AnalyticsEventEnvelope> batch)
    {
        try
        {
            var tokenEvents = batch.OfType<TokenEventEnvelope>().Select(e => e.Data).ToList();
            var snapshots = batch.OfType<SessionSnapshotEnvelope>().Select(e => e.Data).ToList();

            if (tokenEvents.Count > 0 || snapshots.Count > 0)
            {
                await WriteToAnalyticsDbAsync(tokenEvents, snapshots);
                await UpdateMainDbAsync(tokenEvents);
                RecordOtelMetrics(tokenEvents);
            }

            LogBatchFlushed(tokenEvents.Count, snapshots.Count);
        }
        catch (Exception ex)
        {
            LogFlushFailed(ex, batch.Count);
        }
    }

    private async Task WriteToAnalyticsDbAsync(
        IReadOnlyList<TokenEventData> tokenEvents,
        IReadOnlyList<SessionSnapshotData> snapshots)
    {
        using var connection = _analyticsDb.CreateConnection();
        using var tx = connection.BeginTransaction();
        try
        {
            foreach (var evt in tokenEvents)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO token_events (
                        event_id, session_id, project_id, project_name, workspace_directory,
                        model_id, provider_id,
                        tokens_input, tokens_output, tokens_reasoning,
                        tokens_cache_read, tokens_cache_write, tokens_total,
                        cost, estimated_cost, created_at, user_id
                    ) VALUES (
                        @EventId, @SessionId, @ProjectId, @ProjectName, @WorkspaceDirectory,
                        @ModelId, @ProviderId,
                        @TokensInput, @TokensOutput, @TokensReasoning,
                        @TokensCacheRead, @TokensCacheWrite, @TokensTotal,
                        @Cost, @EstimatedCost, @CreatedAt, @UserId
                    )
                    ON CONFLICT(event_id) DO UPDATE SET
                        model_id         = COALESCE(excluded.model_id, token_events.model_id),
                        provider_id      = COALESCE(excluded.provider_id, token_events.provider_id),
                        tokens_input     = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_input     ELSE token_events.tokens_input     END,
                        tokens_output    = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_output    ELSE token_events.tokens_output    END,
                        tokens_reasoning = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_reasoning ELSE token_events.tokens_reasoning END,
                        tokens_cache_read  = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_cache_read  ELSE token_events.tokens_cache_read  END,
                        tokens_cache_write = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_cache_write ELSE token_events.tokens_cache_write END,
                        tokens_total     = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.tokens_total     ELSE token_events.tokens_total     END,
                        cost             = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.cost             ELSE token_events.cost             END,
                        estimated_cost   = CASE WHEN excluded.tokens_total > token_events.tokens_total THEN excluded.estimated_cost   ELSE token_events.estimated_cost   END
                    """,
                    new
                    {
                        evt.EventId, evt.SessionId, evt.ProjectId, evt.ProjectName,
                        evt.WorkspaceDirectory, evt.ModelId, evt.ProviderId,
                        evt.TokensInput, evt.TokensOutput, evt.TokensReasoning,
                        evt.TokensCacheRead, evt.TokensCacheWrite, evt.TokensTotal,
                        evt.Cost, evt.EstimatedCost,
                        CreatedAt = evt.CreatedAt.ToString("O"),
                        evt.UserId
                    },
                    transaction: tx);
            }

            foreach (var snap in snapshots)
            {
                string modelIdsJson = System.Text.Json.JsonSerializer.Serialize(snap.ModelIds, InfrastructureJsonContext.Default.ListString);
                await connection.ExecuteAsync("""
                    INSERT OR REPLACE INTO session_snapshots (
                        session_id, parent_session_id, project_id, project_name,
                        workspace_directory, title, status,
                        total_tokens, total_cost, total_estimated_cost,
                        message_count, model_ids, created_at, ended_at, duration_seconds,
                        user_id
                    ) VALUES (
                        @SessionId, @ParentSessionId, @ProjectId, @ProjectName,
                        @WorkspaceDirectory, @Title, @Status,
                        @TotalTokens, @TotalCost, @TotalEstimatedCost,
                        @MessageCount, @ModelIds, @CreatedAt, @EndedAt, @DurationSeconds,
                        @UserId
                    )
                    """,
                    new
                    {
                        snap.SessionId,
                        snap.ParentSessionId,
                        snap.ProjectId,
                        snap.ProjectName,
                        snap.WorkspaceDirectory,
                        snap.Title,
                        snap.Status,
                        snap.TotalTokens,
                        snap.TotalCost,
                        snap.TotalEstimatedCost,
                        snap.MessageCount,
                        ModelIds = modelIdsJson,
                        CreatedAt = snap.CreatedAt.ToString("O"),
                        EndedAt = snap.EndedAt?.ToString("O"),
                        snap.DurationSeconds,
                        snap.UserId
                    },
                    transaction: tx);
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private async Task UpdateMainDbAsync(List<TokenEventData> tokenEvents)
    {
        if (tokenEvents.Count == 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepository>();

            foreach (var evt in tokenEvents)
            {
                // Guard against double-counting: only increment the main DB once per event_id.
                // If the set is at capacity, evict all entries to prevent unbounded memory growth.
                if (_processedEventIds.Count >= ProcessedEventIdCapacity)
                    _processedEventIds.Clear();

                if (!_processedEventIds.Add(evt.EventId))
                    continue; // already incremented for this event_id

                try
                {
                    // IncrementTokensAsync takes int tokens — round from double
                    await sessionRepo.IncrementTokensAsync(
                        evt.SessionId,
                        (int)Math.Round(evt.TokensTotal),
                        evt.Cost).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Don't let one session failure block the rest
                    LogMainDbUpdateFailed(ex, evt.SessionId);
                }
            }
        }
        catch (Exception ex)
        {
            LogMainDbScopeFailed(ex);
        }
    }

    private static void RecordOtelMetrics(List<TokenEventData> tokenEvents)
    {
        foreach (var data in tokenEvents)
        {
            var tags = new TagList
            {
                { "model", data.ModelId ?? "unknown" },
                { "provider", data.ProviderId ?? "unknown" },
                { "project", data.ProjectId ?? "unknown" }
            };

            FleetInstrumentation.TokensConsumed.Add((long)data.TokensTotal, tags);
            FleetInstrumentation.CostIncurred.Add(data.Cost, tags);

            if (data.EstimatedCost.HasValue)
                FleetInstrumentation.EstimatedCostIncurred.Add(data.EstimatedCost.Value, tags);

            FleetInstrumentation.MessagesProcessed.Add(1, tags);
            FleetInstrumentation.MessageCost.Record(data.Cost, tags);
            FleetInstrumentation.MessageTokens.Record((long)data.TokensTotal, tags);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AnalyticsWriterService started (flushInterval={FlushInterval}, maxBatch={MaxBatch})")]
    private partial void LogStarted(TimeSpan flushInterval, int maxBatch);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Analytics batch flushed: {TokenEvents} token events, {Snapshots} session snapshots")]
    private partial void LogBatchFlushed(int tokenEvents, int snapshots);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Analytics batch flush failed for {BatchSize} events")]
    private partial void LogFlushFailed(Exception ex, int batchSize);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to update main DB token totals for session {SessionId}")]
    private partial void LogMainDbUpdateFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to create scope for main DB token update")]
    private partial void LogMainDbScopeFailed(Exception ex);

}
