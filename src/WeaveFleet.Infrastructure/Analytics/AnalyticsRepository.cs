using System.Data.Common;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Data;

namespace WeaveFleet.Infrastructure.Analytics;

/// <summary>
/// Raw ADO.NET read repository for the analytics database.
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
        var totals = await conn.QueryFirstAsync(
            $"""
            SELECT
                COALESCE(SUM(tokens_total), 0) AS TotalTokens,
                COALESCE(SUM(cost), 0) AS TotalCost,
                COALESCE(SUM(COALESCE(estimated_cost, 0)), 0) AS TotalEstimatedCost,
                COUNT(*) AS MessageCount
            FROM token_events
            {whereClause}
            """,
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userId);
            },
            r => (
                TotalTokens: r.GetDouble(r.GetOrdinal("TotalTokens")),
                TotalCost: r.GetDouble(r.GetOrdinal("TotalCost")),
                TotalEstimatedCost: r.GetDouble(r.GetOrdinal("TotalEstimatedCost")),
                MessageCount: (int)r.GetInt64(r.GetOrdinal("MessageCount"))
            ));

        var sessionCount = (int)(await conn.ExecuteScalarAsync<long>(
            $"""
            SELECT COUNT(DISTINCT session_id)
            FROM token_events
            {whereClause}
            """,
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userId);
            }));

        // Top 5 models by cost
        var topModels = (await conn.QueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userId);
            },
            r => new AnalyticsTopItem(
                r.GetString(r.GetOrdinal("Name")),
                r.GetDouble(r.GetOrdinal("Tokens")),
                r.GetDouble(r.GetOrdinal("Cost"))
            ))).ToList();

        // Top 5 projects by cost
        var topProjects = (await conn.QueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userId);
            },
            r => new AnalyticsTopItem(
                r.GetString(r.GetOrdinal("Name")),
                r.GetDouble(r.GetOrdinal("Tokens")),
                r.GetDouble(r.GetOrdinal("Cost"))
            ))).ToList();

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

        var projectFilter = string.IsNullOrEmpty(projectId)
            ? ""
            : "AND project_id = @ProjectId";

        var dateFilter = BuildDateOnlyFilter(fromStr, toStr);

        var rows = await conn.QueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId ?? "");
                cmd.AddParameter("UserId", userId);
            },
            r => new DailyAnalytics(
                r.GetString(r.GetOrdinal("Date")),
                r.GetDouble(r.GetOrdinal("Tokens")),
                r.GetDouble(r.GetOrdinal("Cost")),
                r.GetDouble(r.GetOrdinal("EstimatedCost")),
                (int)r.GetInt64(r.GetOrdinal("Sessions")),
                (int)r.GetInt64(r.GetOrdinal("Messages"))
            ));

        return rows;
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

        var rows = await conn.QueryAsync(
            $"""
            SELECT
                ss.session_id, ss.title, ss.project_id, ss.project_name,
                CAST(COALESCE(te.agg_tokens, ss.total_tokens) AS REAL) AS total_tokens,
                CAST(COALESCE(te.agg_cost, ss.total_cost) AS REAL) AS total_cost,
                CAST(COALESCE(te.agg_estimated_cost, ss.total_estimated_cost) AS REAL) AS total_estimated_cost,
                COALESCE(te.model_ids, ss.model_ids) AS model_ids,
                ss.duration_seconds, ss.created_at
            FROM session_snapshots ss
            LEFT JOIN (
                SELECT session_id,
                       user_id,
                       SUM(tokens_total) AS agg_tokens,
                       SUM(cost) AS agg_cost,
                       SUM(COALESCE(estimated_cost, 0)) AS agg_estimated_cost,
                       json_group_array(DISTINCT model_id) FILTER (WHERE model_id IS NOT NULL AND model_id != '') AS model_ids
                FROM token_events
                WHERE user_id = @UserId
                GROUP BY session_id, user_id
            ) te ON ss.session_id = te.session_id AND ss.user_id = te.user_id
            {whereClause}
            ORDER BY ss.created_at DESC
            LIMIT @Limit
            """,
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("Limit", limit);
                cmd.AddParameter("UserId", userId);
            },
            r => new SessionAnalytics(
                r.GetString(r.GetOrdinal("session_id")),
                r.GetNullableString(r.GetOrdinal("title")),
                r.GetNullableString(r.GetOrdinal("project_id")),
                r.GetNullableString(r.GetOrdinal("project_name")),
                r.GetDouble(r.GetOrdinal("total_tokens")),
                r.GetDouble(r.GetOrdinal("total_cost")),
                r.GetDouble(r.GetOrdinal("total_estimated_cost")),
                ParseModelIds(r.GetNullableString(r.GetOrdinal("model_ids"))),
                r.GetNullableDouble(r.GetOrdinal("duration_seconds")),
                r.GetString(r.GetOrdinal("created_at"))
            ));

        return rows;
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

        var rows = await conn.QueryAsync(
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
            cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("UserId", userId);
            },
            r => new ModelAnalytics(
                r.GetString(r.GetOrdinal("ModelId")),
                r.GetString(r.GetOrdinal("ProviderId")),
                r.GetDouble(r.GetOrdinal("Tokens")),
                r.GetDouble(r.GetOrdinal("Cost")),
                r.GetDouble(r.GetOrdinal("EstimatedCost")),
                (int)r.GetInt64(r.GetOrdinal("MessageCount")),
                r.GetDouble(r.GetOrdinal("AvgCostPerMessage"))
            ));

        return rows;
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

        var rows = await conn.QueryAsync(
            $"""
            SELECT
                id, event_id, session_id, project_id, project_name,
                workspace_directory, model_id, provider_id,
                tokens_input, tokens_output, tokens_reasoning,
                tokens_cache_read, tokens_cache_write, tokens_total,
                cost, estimated_cost, created_at
            FROM token_events
            {whereClause}
            ORDER BY created_at
            """,
            (Action<DbCommand>)(cmd =>
            {
                cmd.AddParameter("FromDate", fromStr);
                cmd.AddParameter("ToDate", toStr);
                cmd.AddParameter("ProjectId", projectId);
                cmd.AddParameter("UserId", userId);
            }),
            r => new TokenEventRow(
                Id: r.GetInt64(r.GetOrdinal("id")),
                EventId: r.GetString(r.GetOrdinal("event_id")),
                SessionId: r.GetString(r.GetOrdinal("session_id")),
                ProjectId: r.GetNullableString(r.GetOrdinal("project_id")),
                ProjectName: r.GetNullableString(r.GetOrdinal("project_name")),
                WorkspaceDirectory: r.GetNullableString(r.GetOrdinal("workspace_directory")),
                ModelId: r.GetNullableString(r.GetOrdinal("model_id")),
                ProviderId: r.GetNullableString(r.GetOrdinal("provider_id")),
                TokensInput: r.GetDouble(r.GetOrdinal("tokens_input")),
                TokensOutput: r.GetDouble(r.GetOrdinal("tokens_output")),
                TokensReasoning: r.GetDouble(r.GetOrdinal("tokens_reasoning")),
                TokensCacheRead: r.GetDouble(r.GetOrdinal("tokens_cache_read")),
                TokensCacheWrite: r.GetDouble(r.GetOrdinal("tokens_cache_write")),
                TokensTotal: r.GetDouble(r.GetOrdinal("tokens_total")),
                Cost: r.GetDouble(r.GetOrdinal("cost")),
                EstimatedCost: r.GetNullableDouble(r.GetOrdinal("estimated_cost")),
                CreatedAt: r.GetString(r.GetOrdinal("created_at"))
            ));

        return rows;
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
            return System.Text.Json.JsonSerializer.Deserialize(json, InfrastructureJsonContext.Default.ListString) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
