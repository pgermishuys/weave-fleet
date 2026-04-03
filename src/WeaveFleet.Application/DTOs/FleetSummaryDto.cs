namespace WeaveFleet.Application.DTOs;

/// <summary>
/// Fleet-wide summary statistics — matches the frontend FleetSummary shape.
/// </summary>
public sealed record FleetSummaryResponse(
    int ActiveSessions,
    int IdleSessions,
    int TotalTokens,
    double TotalCost,
    int QueuedTasks);
