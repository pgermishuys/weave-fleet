using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// Periodic background service that recomputes <c>daily_rollups</c> from <c>token_events</c>.
/// Runs every N minutes (configurable). Recomputes the last 2 days (UTC) to handle late-arriving events.
/// Follows the <c>HarnessEventRelay</c> pattern for shutdown handling.
/// </summary>
public sealed partial class AnalyticsRollupService : BackgroundService
{
    private readonly IAnalyticsDbConnectionFactory _analyticsDb;
    private readonly FleetOptions _options;
    private readonly ILogger<AnalyticsRollupService> _logger;

    public AnalyticsRollupService(
        IAnalyticsDbConnectionFactory analyticsDb,
        FleetOptions options,
        ILogger<AnalyticsRollupService> logger)
    {
        _analyticsDb = analyticsDb;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rollupInterval = TimeSpan.FromMinutes(_options.AnalyticsRollupIntervalMinutes);
        LogStarted(rollupInterval);

        // Run initial rollup on startup
        await RunRollupAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(rollupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutdown — exit cleanly
                return;
            }

            await RunRollupAsync();
        }
    }

    private async Task RunRollupAsync()
    {
        try
        {
            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            using var conn = _analyticsDb.CreateConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                // Delete existing rollups for the two days we're recomputing
                await conn.ExecuteAsync(
                    "DELETE FROM daily_rollups WHERE date IN (@Today, @Yesterday)",
                    new { Today = today, Yesterday = yesterday },
                    transaction: tx);

                // Recompute per-project, per-model, per-provider rollups (partitioned by user_id)
                await conn.ExecuteAsync(
                    """
                    INSERT INTO daily_rollups (
                        date, user_id, project_id, model_id, provider_id,
                        total_tokens, total_cost, total_estimated_cost,
                        session_count, message_count
                    )
                    SELECT
                        date(created_at) AS date,
                        COALESCE(user_id, '') AS user_id,
                        COALESCE(project_id, '') AS project_id,
                        COALESCE(model_id, '') AS model_id,
                        COALESCE(provider_id, '') AS provider_id,
                        SUM(tokens_total) AS total_tokens,
                        SUM(cost) AS total_cost,
                        SUM(COALESCE(estimated_cost, 0)) AS total_estimated_cost,
                        COUNT(DISTINCT session_id) AS session_count,
                        COUNT(*) AS message_count
                    FROM token_events
                    WHERE date(created_at) IN (@Today, @Yesterday)
                    GROUP BY date(created_at), user_id, project_id, model_id, provider_id
                    """,
                    new { Today = today, Yesterday = yesterday },
                    transaction: tx);

                // Also insert per-user fleet-wide summary rows (project/model/provider all empty = fleet-wide per user)
                await conn.ExecuteAsync(
                    """
                    INSERT OR REPLACE INTO daily_rollups (
                        date, user_id, project_id, model_id, provider_id,
                        total_tokens, total_cost, total_estimated_cost,
                        session_count, message_count
                    )
                    SELECT
                        date(created_at) AS date,
                        COALESCE(user_id, '') AS user_id,
                        '' AS project_id,
                        '' AS model_id,
                        '' AS provider_id,
                        SUM(tokens_total) AS total_tokens,
                        SUM(cost) AS total_cost,
                        SUM(COALESCE(estimated_cost, 0)) AS total_estimated_cost,
                        COUNT(DISTINCT session_id) AS session_count,
                        COUNT(*) AS message_count
                    FROM token_events
                    WHERE date(created_at) IN (@Today, @Yesterday)
                    GROUP BY date(created_at), user_id
                    """,
                    new { Today = today, Yesterday = yesterday },
                    transaction: tx);

                tx.Commit();
                LogRollupCompleted(today, yesterday);
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            LogRollupFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AnalyticsRollupService started (interval={Interval})")]
    private partial void LogStarted(TimeSpan interval);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Daily rollups recomputed for {Today} and {Yesterday}")]
    private partial void LogRollupCompleted(string today, string yesterday);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Analytics rollup computation failed")]
    private partial void LogRollupFailed(Exception ex);
}
