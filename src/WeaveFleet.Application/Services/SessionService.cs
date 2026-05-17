using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Encapsulates business logic for session management and fleet summary.
/// </summary>
public sealed class SessionService(
    ISessionRepository sessionRepository,
    IProjectRepository projectRepository,
    SessionOrchestrator sessionOrchestrator)
{
    public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(
        int limit = 100,
        int offset = 0,
        IReadOnlyList<string>? statuses = null,
        string? projectId = null,
        string? retentionStatus = null)
    {
        IReadOnlyList<string>? retentionStatuses = retentionStatus switch
        {
            null or "" => ["active"],
            "all" => null,
            _ => [retentionStatus]
        };

        var sessions = await sessionRepository.ListAsync(limit, offset, statuses, projectId, retentionStatuses);

        return Result.Success(sessions);
    }

    public async Task<Result<Unit>> StopSessionAsync(string id)
    {
        SetSessionTag(id);
        var result = await sessionOrchestrator.StopSessionAsync(id);
        if (result.IsFailure)
            return result.Error;

        return Unit.Value;
    }

    public async Task<Result<Unit>> UpdateRetentionAsync(string id, string retentionStatus)
    {
        SetSessionTag(id);
        return retentionStatus switch
        {
            "archived" => await sessionOrchestrator.ArchiveSessionAsync(id),
            "active" => await sessionOrchestrator.UnarchiveSessionAsync(id),
            _ => FleetError.ValidationError("Session.RetentionStatus", $"Unsupported retention status '{retentionStatus}'.")
        };
    }

    public async Task<Result<Session>> GetSessionAsync(string id)
    {
        SetSessionTag(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);
        return session;
    }

    public async Task<Result<bool>> DeleteSessionAsync(string id)
    {
        SetSessionTag(id);
        var result = await sessionOrchestrator.DeleteSessionAsync(id);
        if (result.IsFailure)
            return result.Error;

        return true;
    }

    public async Task<Result<Unit>> UpdateSessionTitleAsync(string id, string title)
    {
        SetSessionTag(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        await sessionRepository.UpdateTitleAsync(id, title);
        return Unit.Value;
    }

    public async Task<Result<Unit>> MoveSessionToProjectAsync(string sessionId, string? projectId)
    {
        SetSessionTag(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (projectId is not null)
        {
            var project = await projectRepository.GetByIdAsync(projectId);
            if (project is null)
                return FleetError.NotFoundFor(nameof(Project), projectId);
        }

        await sessionRepository.UpdateProjectAsync(sessionId, projectId);
        return Unit.Value;
    }

    private static void SetSessionTag(string sessionId)
        => Activity.Current?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);

    public async Task<Result<FleetSummary>> GetFleetSummaryAsync()
    {
        var (active, idle) = await sessionRepository.GetStatusCountsAsync();
        var (totalTokens, totalCost) = await sessionRepository.GetFleetTokenTotalsAsync();

        return Result.Success(new FleetSummary
        {
            ActiveSessions = active,
            IdleSessions = idle,
            TotalTokens = totalTokens,
            TotalCost = totalCost,
            QueuedTasks = 0  // placeholder — Phase 5 will implement real task queue
        });
    }
}

/// <summary>
/// Aggregated fleet statistics.
/// </summary>
public sealed class FleetSummary
{
    public int ActiveSessions { get; init; }
    public int IdleSessions { get; init; }
    public int TotalTokens { get; init; }
    public double TotalCost { get; init; }
    public int QueuedTasks { get; init; }
}
