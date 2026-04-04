namespace WeaveFleet.Application.Analytics;

/// <summary>
/// Read-side interface for querying analytics data. Used by API endpoints.
/// Implementations query the analytics SQLite database.
/// </summary>
public interface IAnalyticsReader
{
    Task<AnalyticsSummary> GetSummaryAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyAnalytics>> GetDailyAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionAnalytics>> GetSessionsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelAnalytics>> GetModelsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TokenEventRow>> ExportTokenEventsAsync(
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate,
        string? projectId,
        CancellationToken cancellationToken = default);
}
