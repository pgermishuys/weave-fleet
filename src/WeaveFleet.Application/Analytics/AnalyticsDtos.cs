namespace WeaveFleet.Application.Analytics;

// ─── Response DTOs ────────────────────────────────────────────────────────────

/// <summary>Aggregated analytics summary across an optional date/project range.</summary>
public sealed record AnalyticsSummary(
    double TotalTokens,
    double TotalCost,
    double TotalEstimatedCost,
    int SessionCount,
    int MessageCount,
    IReadOnlyList<AnalyticsTopItem> TopModels,
    IReadOnlyList<AnalyticsTopItem> TopProjects);

/// <summary>A ranked item in a top-N list (model or project).</summary>
public sealed record AnalyticsTopItem(
    string Name,
    double Tokens,
    double Cost);

/// <summary>Per-day aggregated analytics from the daily_rollups table.</summary>
public sealed record DailyAnalytics(
    string Date,
    double Tokens,
    double Cost,
    double EstimatedCost,
    int Sessions,
    int Messages);

/// <summary>Per-session analytics from the session_snapshots table.</summary>
public sealed record SessionAnalytics(
    string SessionId,
    string? Title,
    string? ProjectId,
    string? ProjectName,
    double Tokens,
    double Cost,
    double EstimatedCost,
    IReadOnlyList<string> Models,
    double? DurationSeconds,
    string CreatedAt);

/// <summary>Per-model aggregated analytics.</summary>
public sealed record ModelAnalytics(
    string ModelId,
    string ProviderId,
    double Tokens,
    double Cost,
    double EstimatedCost,
    int MessageCount,
    double AvgCostPerMessage);

/// <summary>A raw row from the token_events table for CSV/JSON export.</summary>
public sealed record TokenEventRow(
    long Id,
    string EventId,
    string SessionId,
    string? ProjectId,
    string? ProjectName,
    string? WorkspaceDirectory,
    string? ModelId,
    string? ProviderId,
    double TokensInput,
    double TokensOutput,
    double TokensReasoning,
    double TokensCacheRead,
    double TokensCacheWrite,
    double TokensTotal,
    double Cost,
    double? EstimatedCost,
    string CreatedAt);
