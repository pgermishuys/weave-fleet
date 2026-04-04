using System.Globalization;
using System.Text;
using WeaveFleet.Application.Analytics;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Analytics query endpoints: summary, daily breakdown, sessions, models, and raw export.
/// All endpoints are read-only and require analytics to be enabled.
/// </summary>
public static class AnalyticsEndpoints
{
    public static WebApplication MapAnalyticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/analytics").WithTags("Analytics");

        // GET /api/analytics/summary?from=&to=&projectId=
        group.MapGet("/summary", async (
            string? from,
            string? to,
            string? projectId,
            IAnalyticsReader? reader) =>
        {
            if (reader is null)
                return Results.Problem("Analytics is disabled.", statusCode: 503);

            var summary = await reader.GetSummaryAsync(ParseDate(from), ParseDate(to), projectId);
            return Results.Ok(summary);
        })
        .Produces<AnalyticsSummary>(200)
        .WithName("GetAnalyticsSummary");

        // GET /api/analytics/daily?from=&to=&projectId=
        group.MapGet("/daily", async (
            string? from,
            string? to,
            string? projectId,
            IAnalyticsReader? reader) =>
        {
            if (reader is null)
                return Results.Problem("Analytics is disabled.", statusCode: 503);

            var daily = await reader.GetDailyAsync(ParseDate(from), ParseDate(to), projectId);
            return Results.Ok(daily);
        })
        .Produces<IReadOnlyList<DailyAnalytics>>(200)
        .WithName("GetAnalyticsDaily");

        // GET /api/analytics/sessions?from=&to=&projectId=&limit=50
        group.MapGet("/sessions", async (
            string? from,
            string? to,
            string? projectId,
            int? limit,
            IAnalyticsReader? reader) =>
        {
            if (reader is null)
                return Results.Problem("Analytics is disabled.", statusCode: 503);

            var sessions = await reader.GetSessionsAsync(ParseDate(from), ParseDate(to), projectId, limit ?? 50);
            return Results.Ok(sessions);
        })
        .Produces<IReadOnlyList<SessionAnalytics>>(200)
        .WithName("GetAnalyticsSessions");

        // GET /api/analytics/models?from=&to=
        group.MapGet("/models", async (
            string? from,
            string? to,
            IAnalyticsReader? reader) =>
        {
            if (reader is null)
                return Results.Problem("Analytics is disabled.", statusCode: 503);

            var models = await reader.GetModelsAsync(ParseDate(from), ParseDate(to));
            return Results.Ok(models);
        })
        .Produces<IReadOnlyList<ModelAnalytics>>(200)
        .WithName("GetAnalyticsModels");

        // GET /api/analytics/export?from=&to=&projectId=&format=json
        group.MapGet("/export", async (
            string? from,
            string? to,
            string? projectId,
            string? format,
            IAnalyticsReader? reader) =>
        {
            if (reader is null)
                return Results.Problem("Analytics is disabled.", statusCode: 503);

            var rows = await reader.ExportTokenEventsAsync(ParseDate(from), ParseDate(to), projectId);

            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                var csv = BuildCsv(rows);
                return Results.Content(csv, "text/csv", Encoding.UTF8);
            }

            return Results.Ok(rows);
        })
        .Produces<IReadOnlyList<TokenEventRow>>(200)
        .WithName("ExportAnalyticsTokenEvents");

        return app;
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    private static string BuildCsv(IReadOnlyList<TokenEventRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,event_id,session_id,project_id,project_name,workspace_directory," +
                      "model_id,provider_id,tokens_input,tokens_output,tokens_reasoning," +
                      "tokens_cache_read,tokens_cache_write,tokens_total,cost,estimated_cost,created_at");

        foreach (var r in rows)
        {
            sb.Append(r.Id); sb.Append(',');
            AppendCsvField(sb, r.EventId); sb.Append(',');
            AppendCsvField(sb, r.SessionId); sb.Append(',');
            AppendCsvField(sb, r.ProjectId); sb.Append(',');
            AppendCsvField(sb, r.ProjectName); sb.Append(',');
            AppendCsvField(sb, r.WorkspaceDirectory); sb.Append(',');
            AppendCsvField(sb, r.ModelId); sb.Append(',');
            AppendCsvField(sb, r.ProviderId); sb.Append(',');
            sb.Append(r.TokensInput.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.TokensOutput.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.TokensReasoning.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.TokensCacheRead.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.TokensCacheWrite.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.TokensTotal.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.Cost.ToString(CultureInfo.InvariantCulture)); sb.Append(',');
            sb.Append(r.EstimatedCost?.ToString(CultureInfo.InvariantCulture) ?? ""); sb.Append(',');
            AppendCsvField(sb, r.CreatedAt);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendCsvField(StringBuilder sb, string? value)
    {
        if (value is null)
            return;

        if (value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            sb.Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }
}
