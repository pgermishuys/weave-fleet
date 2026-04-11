using Dapper;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// Dapper-based read repository for the analytics database.
/// Implements <see cref="IAnalyticsReader"/> with parameterized queries.
/// Date range filtering uses ISO 8601 string comparison (valid for SQLite with UTC timestamps).
/// All queries are scoped by <see cref="IUserContext.UserId"/> for tenant isolation.
/// </summary>
public sealed class AnalyticsRepository(
    IAnalyticsDbConnectionFactory connectionFactory,
    IUserContext userContext) : IAnalyticsReader
{
    public async Task<AnalyticsSummary> GetSummaryAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        using var conn = connectionFactory.CreateConnection();

        var fromStr = fromDate?.ToString("O");
        var toStr = toDate?.ToString("O");
        var userId = userContext.UserId;

        var whereClause = BuildWhereClause(fromStr, toStr, projectId);

        // Aggregate totals
        var totals = await conn.QuerySingleOrDefaultAsync<(double TotalTokens, double TotalCost, double TotalEstimatedCost, int MessageCount)>(
            $"""
            SELECT
                COALESCE(SUM(tokens_total), 0) AS TotalTokens,
                COALESCE(SUM(cost), 0) AS TotalCost,
                COALESCE(SUM(COALESCE(estimated_cost, 0)), 0) AS TotalEstimatedCost,
                COUNT(*) AS MessageCount
            FROM token_events
            {whereClause}
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, UserId = userId });

        var sessionCount = await conn.ExecuteScalarAsync<int>(
            $"""
            SELECT COUNT(DISTINCT session_id)
            FROM token_events
            {whereClause}
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, UserId = userId });

        // Top 5 models by cost
        var topModels = (await conn.QueryAsync<(string Name, double Tokens, double Cost)>(
            $"""
            SELECT
                COALESCE(model_id, 'unknown') AS Name,
                COALESCE(SUM(tokens_total), 0) AS Tokens,
                COALESCE(SUM(cost), 0) AS Cost
            FROM token_events
            {whereClause}
            GROUP BY model_id
            ORDER BY Cost DESC
            LIMIT 5
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, UserId = userId }))
            .Select(r => new AnalyticsTopItem(r.Name, r.Tokens, r.Cost))
            .ToList();

        // Top 5 projects by cost
        var topProjects = (await conn.QueryAsync<(string Name, double Tokens, double Cost)>(
            $"""
            SELECT
                COALESCE(project_name, project_id, 'unknown') AS Name,
                COALESCE(SUM(tokens_total), 0) AS Tokens,
                COALESCE(SUM(cost), 0) AS Cost
            FROM token_events
            {whereClause}
            GROUP BY project_id
            ORDER BY Cost DESC
            LIMIT 5
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, UserId = userId }))
            .Select(r => new AnalyticsTopItem(r.Name, r.Tokens, r.Cost))
            .ToList();

        return new AnalyticsSummary(
            totals.TotalTokens, totals.TotalCost, totals.TotalEstimatedCost,
            sessionCount, totals.MessageCount,
            topModels, topProjects);
    }

    public async Task<IReadOnlyList<DailyAnalytics>> GetDailyAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        using var conn = connectionFactory.CreateConnection();

        var fromStr = fromDate?.ToString("O");
        var toStr = toDate?.ToString("O");
        var userId = userContext.UserId;

        // Read from daily_rollups — fast path. Falls back to empty if no rollups computed yet.
        // When no project filter, aggregate all rows (fleet-wide for this user). When project specified, filter.
        var projectFilter = string.IsNullOrEmpty(projectId)
            ? ""
            : "AND project_id = @ProjectId";

        var dateFilter = BuildDateOnlyFilter(fromStr, toStr);

        var rows = await conn.QueryAsync<DailyRollupRow>(
            $"""
            SELECT
                date AS Date,
                COALESCE(SUM(total_tokens), 0.0) AS Tokens,
                COALESCE(SUM(total_cost), 0.0) AS Cost,
                COALESCE(SUM(total_estimated_cost), 0.0) AS EstimatedCost,
                COALESCE(SUM(session_count), 0) AS Sessions,
                COALESCE(SUM(message_count), 0) AS Messages
            FROM daily_rollups
            WHERE 1=1
            AND user_id = @UserId
            {dateFilter}
            {projectFilter}
            GROUP BY date
            ORDER BY date
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId ?? "", UserId = userId });

        return rows
            .Select(r => new DailyAnalytics(r.Date, r.Tokens, r.Cost, r.EstimatedCost, (int)r.Sessions, (int)r.Messages))
            .ToList();
    }

    public async Task<IReadOnlyList<SessionAnalytics>> GetSessionsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var conn = connectionFactory.CreateConnection();

        var fromStr = fromDate?.ToString("O");
        var toStr = toDate?.ToString("O");
        var userId = userContext.UserId;

        var conditions = new List<string> { "ss.user_id = @UserId" };
        if (fromStr is not null) conditions.Add("ss.created_at >= @FromDate");
        if (toStr is not null) conditions.Add("ss.created_at < @ToDate");
        if (!string.IsNullOrEmpty(projectId)) conditions.Add("ss.project_id = @ProjectId");

        var whereClause = "WHERE " + string.Join(" AND ", conditions);

        // Join token_events to get live token/cost totals instead of relying on
        // the snapshot values which are only set at session creation (0) and deletion.
        var rows = await conn.QueryAsync<SessionSnapshotRow>(
            $"""
            SELECT
                ss.session_id, ss.title, ss.project_id, ss.project_name,
                CAST(COALESCE(te.agg_tokens, ss.total_tokens) AS REAL) AS total_tokens,
                CAST(COALESCE(te.agg_cost, ss.total_cost) AS REAL) AS total_cost,
                CAST(COALESCE(te.agg_estimated_cost, ss.total_estimated_cost) AS REAL) AS total_estimated_cost,
                ss.model_ids, ss.duration_seconds, ss.created_at
            FROM session_snapshots ss
            LEFT JOIN (
                SELECT session_id,
                       user_id,
                       SUM(tokens_total) AS agg_tokens,
                       SUM(cost) AS agg_cost,
                       SUM(COALESCE(estimated_cost, 0)) AS agg_estimated_cost
                FROM token_events
                WHERE user_id = @UserId
                GROUP BY session_id, user_id
            ) te ON ss.session_id = te.session_id AND ss.user_id = te.user_id
            {whereClause}
            ORDER BY ss.created_at DESC
            LIMIT @Limit
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, Limit = limit, UserId = userId });

        return rows
            .Select(r => new SessionAnalytics(
                r.SessionId, r.Title, r.ProjectId, r.ProjectName,
                r.TotalTokens, r.TotalCost, r.TotalEstimatedCost,
                ParseModelIds(r.ModelIds),
                r.DurationSeconds, r.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<ModelAnalytics>> GetModelsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        CancellationToken cancellationToken = default)
    {
        using var conn = connectionFactory.CreateConnection();

        var fromStr = fromDate?.ToString("O");
        var toStr = toDate?.ToString("O");
        var userId = userContext.UserId;

        var dateFilter = BuildDateFilter(fromStr, toStr);

        var rows = await conn.QueryAsync<ModelAnalyticsRow>(
            $"""
            SELECT
                COALESCE(model_id, 'unknown') AS ModelId,
                COALESCE(provider_id, 'unknown') AS ProviderId,
                COALESCE(SUM(tokens_total), 0.0) AS Tokens,
                COALESCE(SUM(cost), 0.0) AS Cost,
                COALESCE(SUM(COALESCE(estimated_cost, 0.0)), 0.0) AS EstimatedCost,
                COUNT(*) AS MessageCount,
                CASE WHEN COUNT(*) > 0
                    THEN COALESCE(SUM(cost), 0.0) / COUNT(*)
                    ELSE 0.0 END AS AvgCostPerMessage
            FROM token_events
            WHERE 1=1 AND user_id = @UserId {dateFilter}
            GROUP BY model_id, provider_id
            ORDER BY Cost DESC
            """,
            new { FromDate = fromStr, ToDate = toStr, UserId = userId });

        return rows
            .Select(r => new ModelAnalytics(r.ModelId, r.ProviderId, r.Tokens, r.Cost, r.EstimatedCost, (int)r.MessageCount, r.AvgCostPerMessage))
            .ToList();
    }

    public async Task<IReadOnlyList<TokenEventRow>> ExportTokenEventsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        using var conn = connectionFactory.CreateConnection();

        var fromStr = fromDate?.ToString("O");
        var toStr = toDate?.ToString("O");
        var userId = userContext.UserId;

        var whereClause = BuildWhereClause(fromStr, toStr, projectId);

        var rows = await conn.QueryAsync<TokenEventRow>(
            $"""
            SELECT
                id AS Id,
                event_id AS EventId,
                session_id AS SessionId,
                project_id AS ProjectId,
                project_name AS ProjectName,
                workspace_directory AS WorkspaceDirectory,
                model_id AS ModelId,
                provider_id AS ProviderId,
                tokens_input AS TokensInput,
                tokens_output AS TokensOutput,
                tokens_reasoning AS TokensReasoning,
                tokens_cache_read AS TokensCacheRead,
                tokens_cache_write AS TokensCacheWrite,
                tokens_total AS TokensTotal,
                cost AS Cost,
                estimated_cost AS EstimatedCost,
                created_at AS CreatedAt
            FROM token_events
            {whereClause}
            ORDER BY created_at
            """,
            new { FromDate = fromStr, ToDate = toStr, ProjectId = projectId, UserId = userId });

        return rows.ToList();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildWhereClause(string? fromStr, string? toStr, string? projectId)
    {
        var parts = new List<string> { "user_id = @UserId" };
        if (fromStr is not null) parts.Add("created_at >= @FromDate");
        if (toStr is not null) parts.Add("created_at < @ToDate");
        if (!string.IsNullOrEmpty(projectId)) parts.Add("project_id = @ProjectId");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildDateFilter(string? fromStr, string? toStr)
    {
        var parts = new List<string>();
        if (fromStr is not null) parts.Add("AND created_at >= @FromDate");
        if (toStr is not null) parts.Add("AND created_at < @ToDate");
        return string.Join(" ", parts);
    }

    private static string BuildDateOnlyFilter(string? fromStr, string? toStr)
    {
        var parts = new List<string>();
        if (fromStr is not null) parts.Add("AND date >= date(@FromDate)");
        if (toStr is not null) parts.Add("AND date < date(@ToDate)");
        return string.Join(" ", parts);
    }

    private static List<string> ParseModelIds(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    // Internal projection helpers — use mutable classes with default constructors so Dapper
    // can map by property name (positional records fail with aggregate REAL/INTEGER columns
    // because SQLite returns unexpected CLR types like byte[] for COALESCE(SUM(...),0)).
#pragma warning disable CA1812 // Internal class is never instantiated (Dapper instantiates via reflection)
    private sealed class SessionSnapshotRow
    {
        public string SessionId { get; init; } = "";
        public string? Title { get; init; }
        public string? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public double TotalTokens { get; init; }
        public double TotalCost { get; init; }
        public double TotalEstimatedCost { get; init; }
        public string? ModelIds { get; init; }
        public double? DurationSeconds { get; init; }
        public string CreatedAt { get; init; } = "";
    }

    private sealed class DailyRollupRow
    {
        public string Date { get; init; } = "";
        public double Tokens { get; init; }
        public double Cost { get; init; }
        public double EstimatedCost { get; init; }
        public long Sessions { get; init; }
        public long Messages { get; init; }
    }

    private sealed class ModelAnalyticsRow
    {
        public string ModelId { get; init; } = "";
        public string ProviderId { get; init; } = "";
        public double Tokens { get; init; }
        public double Cost { get; init; }
        public double EstimatedCost { get; init; }
        public long MessageCount { get; init; }
        public double AvgCostPerMessage { get; init; }
    }
#pragma warning restore CA1812
}
