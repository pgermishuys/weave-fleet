using System.Diagnostics;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Encapsulates business logic for session management and fleet summary.
/// </summary>
public sealed class SessionService(
    ISessionRepository sessionRepository,
    IProjectRepository projectRepository,
    SessionOrchestrator sessionOrchestrator,
    SessionActivityTracker activityTracker)
{
    public async Task<Result<IReadOnlyList<Session>>> ListSessionsAsync(
        int limit = 100,
        int offset = 0,
        IReadOnlyList<string>? statuses = null,
        string? projectId = null,
        string? retentionStatus = null,
        IReadOnlyList<string>? tags = null)
    {
        IReadOnlyList<string>? retentionStatuses = retentionStatus switch
        {
            null or "" => ["active"],
            "all" => null,
            _ => [retentionStatus]
        };

        var sessions = await sessionRepository.ListAsync(limit, offset, statuses, projectId, retentionStatuses, tags);

        return Result.Success(sessions);
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

    public async Task<Result<Session>> UpdateSessionTagsAsync(string id, List<string> tags)
    {
        SetSessionTag(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        await sessionRepository.UpdateTagsAsync(id, tags);
        
        // Fetch the updated session to return
        var updatedSession = await sessionRepository.GetByIdAsync(id);
        return updatedSession!;
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

        // A workflow run's steps show as one group in the Sessions list, so they move together; later steps follow.
        var moving = session.WorkflowRunId is { } runId
            ? await sessionRepository.GetForWorkflowRunAsync(runId)
            : [session];
        foreach (var each in moving)
            await sessionRepository.UpdateProjectAsync(each.Id, projectId);
        return Unit.Value;
    }

    /// <summary>
    /// Moves a fork or a session an agent started out of the session it came from, so it stands on its own
    /// (<paramref name="detached"/>), or back under it. Where it came from is kept either way. A subagent's session
    /// belongs to its parent's turn and can't be moved out.
    /// </summary>
    public async Task<Result<Unit>> SetLineageDetachedAsync(string sessionId, bool detached)
    {
        SetSessionTag(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (session.ParentSessionId is not null)
            return FleetError.ValidationError("Lineage", "A subagent's session belongs to its parent's turn; it can't be moved out.");
        if (!SessionLineage.CanDetach(session))
            return FleetError.ValidationError("Lineage", "This session didn't come from another session.");

        // Moving it out again keeps when it first was.
        if (detached == session.LineageDetachedAt is not null)
            return Unit.Value;

        await sessionRepository.UpdateLineageDetachedAsync(sessionId, detached ? DateTime.UtcNow.ToString("O") : null);
        return Unit.Value;
    }

    /// <summary>
    /// Pins the session in the Pinned group above the projects, just before <paramref name="beforeSessionId"/>, or at
    /// the end when that's null or isn't pinned. Pinning one that's already pinned moves it. Returns its new order.
    /// </summary>
    public async Task<Result<double>> PinSessionAsync(string sessionId, string? beforeSessionId)
    {
        SetSessionTag(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);
        if (session.ParentSessionId is not null)
            return FleetError.ValidationError("Pin", "A subagent's session isn't in the list; pin the session it belongs to.");
        if (session.RetentionStatus == "archived")
            return FleetError.ValidationError("Pin", "Archived sessions can't be pinned; restore it first.");

        var others = (await sessionRepository.ListPinnedAsync()).Where(p => p.Id != sessionId).ToList();
        var at = beforeSessionId is null ? -1 : others.FindIndex(p => p.Id == beforeSessionId);
        if (at < 0)
        {
            var last = others.Count == 0 ? 0 : others[^1].PinOrder;
            await sessionRepository.UpdatePinOrderAsync(sessionId, last + 1);
            return last + 1;
        }

        var after = at == 0 ? others[0].PinOrder - 1 : others[at - 1].PinOrder;
        var order = (after + others[at].PinOrder) / 2;
        if (order > after && order < others[at].PinOrder)
        {
            await sessionRepository.UpdatePinOrderAsync(sessionId, order);
            return order;
        }

        // Halved too often to fit between them: number every pin again, 1, 2, 3, with this one in its place.
        others.Insert(at, (sessionId, 0));
        for (var i = 0; i < others.Count; i++)
            await sessionRepository.UpdatePinOrderAsync(others[i].Id, i + 1);
        return at + 1;
    }

    /// <summary>Unpins the session: it goes back to its project, newest first. Unpinning one that isn't pinned does nothing.</summary>
    public async Task<Result<Unit>> UnpinSessionAsync(string sessionId)
    {
        SetSessionTag(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (session.PinOrder is not null)
            await sessionRepository.UpdatePinOrderAsync(sessionId, null);
        return Unit.Value;
    }

    private static void SetSessionTag(string sessionId)
        => Activity.Current?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);

    public async Task<Result<FleetSummary>> GetFleetSummaryAsync()
    {
        // Get all active sessions and compute counts from the tracker
        var activeSessions = await sessionRepository.ListActiveAsync();
        var activeCount = 0;
        var idleCount = 0;

        // The sessions the home page lists. A side conversation (/btw) is only ever seen beside its session, and a
        // subagent's child session under its parent: neither is one of the fleet's. Background work is counted by the
        // status bar, not here.
        foreach (var session in activeSessions.Where(s => s.SideOfSessionId is null && s.ParentSessionId is null && !s.IsHidden))
        {
            var effectiveStatus = activityTracker.GetEffectiveActivityStatus(session.Id) ?? "idle";
            // A session stopped on a question is mid-turn, not idle.
            if (SessionActivityTracker.IsInTurn(effectiveStatus))
                activeCount++;
            else
                idleCount++;
        }

        var (totalTokens, totalCost) = await sessionRepository.GetFleetTokenTotalsAsync();

        return Result.Success(new FleetSummary
        {
            ActiveSessions = activeCount,
            IdleSessions = idleCount,
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
