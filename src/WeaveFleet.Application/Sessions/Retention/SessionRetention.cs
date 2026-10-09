using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Retention;

/// <summary>
/// Archiving, restoring and deleting sessions. Archiving keeps the conversation (and the harness session) but ends the
/// session's terminals, apps and what its agent left running; deleting removes the harness session, its workspace, its
/// delegations and smart links, the session itself, then its screenshots and pages. A side conversation is deleted
/// with its session, leaving their shared folder alone.
/// </summary>
public sealed partial class SessionRetention(
    WorkspaceService workspaceService,
    InstanceService instanceService,
    InstanceTracker instanceTracker,
    ISessionRepository sessionRepository,
    IDelegationRepository delegationRepository,
    ISmartLinkRepository smartLinkRepository,
    IEventBroadcaster eventBroadcaster,
    IAnalyticsCollector analyticsCollector,
    SessionActivityTracker sessionActivityTracker,
    ILogger<SessionRetention> logger,
    SessionActivityWriteService? sessionActivityWriteService = null,
    ISessionTerminalCleanup? sessionTerminals = null,
    ISessionAppCleanup? sessionApps = null,
    SessionRecapService? sessionRecaps = null,
    SessionNotifier? sessionNotifier = null,
    ISessionScreenshotStore? sessionScreenshots = null,
    IPageStore? sessionPages = null)
{
    public async Task<Result<Unit>> ArchiveSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return Unit.Value;

        var archivedAt = DateTime.UtcNow.ToString("O");
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.ArchiveAsync(id, archivedAt);
            await eventBroadcaster.BroadcastAsync("sessions", "session_archived",
                JsonSerializer.SerializeToElement(new SessionArchivedOutboxPayload(id, archivedAt), ApplicationJsonContext.Default.SessionArchivedOutboxPayload),
                session.UserId, ct);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    SessionArchives = [new SessionArchiveUpdate { SessionId = id, ArchivedAt = archivedAt }],
                    OutboxMessages =
                    [
                        CreateSessionLifecycleOutboxMessage(
                            "session_archived",
                            JsonSerializer.Serialize(new SessionArchivedOutboxPayload(id, archivedAt), ApplicationJsonContext.Default.SessionArchivedOutboxPayload),
                            archivedAt,
                            session.UserId)
                    ]
                },
                ct);
        }

        await EndTerminalsAsync(id, ct);
        await StopAppsAsync(id, ct);
        await ArchiveInstanceAsync(session, ct);
        return Unit.Value;
    }

    /// <summary>
    /// Ends what the session's agent started and left running (<see cref="IHarnessSession.ArchiveAsync"/>). Best effort:
    /// it never fails the caller.
    /// </summary>
    private async Task ArchiveInstanceAsync(Session session, CancellationToken ct)
    {
        if (instanceTracker.Get(session.InstanceId) is not { } instance)
            return;

        try
        {
            await instance.ArchiveAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogArchiveInstanceFailed(ex, session.Id);
        }
    }

    /// <summary>Stops the apps Fleet runs for the session (dev servers). Best effort: it never fails the caller.</summary>
    private async Task StopAppsAsync(string sessionId, CancellationToken ct)
    {
        if (sessionApps is null)
            return;

        try
        {
            await sessionApps.StopSessionAppsAsync(sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAppCleanupFailed(ex, sessionId);
        }
    }

    /// <summary>Ends the session's terminals and deletes their scrollback. Best effort: it never fails the caller.</summary>
    private async Task EndTerminalsAsync(string sessionId, CancellationToken ct)
    {
        if (sessionTerminals is null)
            return;

        try
        {
            await sessionTerminals.EndSessionAsync(sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogTerminalCleanupFailed(ex, sessionId);
        }
    }

    /// <summary>Deletes the pages the session's agents showed. Best effort: it never fails the caller.</summary>
    private async Task DeletePagesAsync(string sessionId, CancellationToken ct)
    {
        if (sessionPages is null)
            return;

        try
        {
            await sessionPages.DeleteSessionAsync(sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPageCleanupFailed(ex, sessionId);
        }
    }

    /// <summary>Deletes the screenshots the session's agents took. Best effort: it never fails the caller.</summary>
    private async Task DeleteScreenshotsAsync(string sessionId, CancellationToken ct)
    {
        if (sessionScreenshots is null)
            return;

        try
        {
            await sessionScreenshots.DeleteSessionAsync(sessionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScreenshotCleanupFailed(ex, sessionId);
        }
    }

    /// <summary>Brings an archived session back to the active list. Its conversation is intact; the terminals archiving ended stay gone.</summary>
    public async Task<Result<Unit>> UnarchiveSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (!string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return Unit.Value;

        var changedAt = DateTime.UtcNow.ToString("O");
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.UnarchiveAsync(id);
            await eventBroadcaster.BroadcastAsync("sessions", "session_unarchived",
                JsonSerializer.SerializeToElement(new SessionUnarchivedOutboxPayload(id), ApplicationJsonContext.Default.SessionUnarchivedOutboxPayload),
                session.UserId, ct);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    SessionUnarchives = [id],
                    OutboxMessages =
                    [
                        CreateSessionLifecycleOutboxMessage(
                            "session_unarchived",
                            JsonSerializer.Serialize(new SessionUnarchivedOutboxPayload(id), ApplicationJsonContext.Default.SessionUnarchivedOutboxPayload),
                            changedAt,
                            session.UserId)
                    ]
                },
                ct);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> DeleteSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        // A side conversation shares its session's folder, which must outlive it; and it goes with its session.
        if (session.SideOfSessionId is not null)
        {
            await DiscardSideConversationAsync(session, ct).ConfigureAwait(false);
            return Unit.Value;
        }

        foreach (var side in await sessionRepository.ListSideConversationsAsync(id).ConfigureAwait(false))
            await DiscardSideConversationAsync(side, ct).ConfigureAwait(false);

        var delegation = await delegationRepository.GetByChildSessionIdAsync(id);
        var parentDelegations = await delegationRepository.GetByParentSessionIdAsync(id);

        // Stop live instance if running
        var liveInstance = instanceTracker.Get(session.InstanceId);
        if (liveInstance is not null)
        {
            await SafeDeleteAsync(liveInstance, ct);
            instanceTracker.Remove(session.InstanceId);
        }

        sessionRecaps?.Forget(id);
        sessionNotifier?.Forget(id);

        // End the session's terminals and apps before its folder goes: a process inside a worktree keeps it open on Windows.
        await EndTerminalsAsync(id, ct);
        await StopAppsAsync(id, ct);

        // Clean up workspace directory (worktree/clone) — best effort, must not block deletion
        try
        {
            await workspaceService.CleanupWorkspaceAsync(session.WorkspaceId);
        }
        catch (Exception ex)
        {
            LogStopFailed(ex, session.InstanceId);
        }

        var deletedAt = DateTime.UtcNow;
        var instanceUpdateResult = await instanceService.UpdateInstanceStatusAsync(
            session.InstanceId, "stopped", deletedAt.ToString("O"));
        if (instanceUpdateResult.IsFailure)
            return instanceUpdateResult.Error;

        var deletedAtText = deletedAt.ToString("O");
        var delegationTerminalStatus = delegation is null ? null : GetDelegationTerminalStatus(session.Status);
        if (sessionActivityWriteService is null)
        {
            if (delegation is not null && delegationTerminalStatus is not null)
            {
                delegation.Status = delegationTerminalStatus;
                delegation.ChildSessionId = null;
                delegation.UpdatedAt = deletedAtText;
                delegation.CompletedAt = deletedAtText;

                await delegationRepository.UpdateStatusAsync(delegation.Id, delegationTerminalStatus, deletedAtText, deletedAtText);
                await delegationRepository.UpdateChildSessionIdAsync(delegation.Id, null, deletedAtText);
                await eventBroadcaster.BroadcastAsync(
                    $"session:{delegation.ParentSessionId}",
                    "delegation.updated",
                    JsonSerializer.SerializeToElement(new DelegationEventDto(
                        delegation.Id,
                        delegation.ParentSessionId,
                        delegation.ParentToolCallId,
                        delegation.ChildSessionId,
                        delegation.Title,
                        delegation.Status,
                        delegation.CreatedAt), ApplicationJsonContext.Default.DelegationEventDto),
                    session.UserId,
                    ct);
            }

            if (parentDelegations.Count > 0)
                await delegationRepository.DeleteByParentSessionIdAsync(id);

            await smartLinkRepository.DeleteBySessionIdAsync(id);
            await sessionRepository.DeleteAsync(id);
            await eventBroadcaster.BroadcastAsync("sessions", "session_deleted",
                JsonSerializer.SerializeToElement(new SessionDeletedOutboxPayload(id), ApplicationJsonContext.Default.SessionDeletedOutboxPayload),
                session.UserId, ct);
        }
        else
        {
            var outboxMessages = new List<OutboxMessage>();
            if (delegation is not null && delegationTerminalStatus is not null)
            {
                delegation.Status = delegationTerminalStatus;
                delegation.ChildSessionId = null;
                delegation.UpdatedAt = deletedAtText;
                delegation.CompletedAt = deletedAtText;

                outboxMessages.Add(new OutboxMessage
                {
                    Topic = $"session:{delegation.ParentSessionId}",
                    Type = "delegation.updated",
                    Payload = JsonSerializer.Serialize(new DelegationEventDto(
                        delegation.Id,
                        delegation.ParentSessionId,
                        delegation.ParentToolCallId,
                        delegation.ChildSessionId,
                        delegation.Title,
                        delegation.Status,
                        delegation.CreatedAt),
                        ApplicationJsonContext.Default.DelegationEventDto),
                    UserId = session.UserId,
                    CreatedAt = deletedAtText,
                    AvailableAt = deletedAtText
                });
            }

            outboxMessages.Add(
                CreateSessionLifecycleOutboxMessage(
                    "session_deleted",
                    JsonSerializer.Serialize(new SessionDeletedOutboxPayload(id), ApplicationJsonContext.Default.SessionDeletedOutboxPayload),
                    deletedAtText,
                    session.UserId));

            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    DelegationStatusUpdates = delegation is not null && delegationTerminalStatus is not null
                        ? [new DelegationStatusUpdate
                        {
                            Id = delegation.Id,
                            Status = delegationTerminalStatus,
                            UpdatedAt = deletedAtText,
                            CompletedAt = deletedAtText
                        }]
                        : [],
                    DelegationChildSessionUpdates = delegation is not null
                        ? [new DelegationChildSessionUpdate
                        {
                            Id = delegation.Id,
                            ChildSessionId = null,
                            UpdatedAt = deletedAtText
                        }]
                        : [],
                    DelegationDeletesByParentSessionId = parentDelegations.Count > 0 ? [id] : [],
                    SmartLinkDeletesBySessionId = [id],
                    SessionDeletes = [id],
                    OutboxMessages = outboxMessages
                },
                ct);
        }

        // Only once the session is gone: an archived one keeps its screenshots and pages, since its conversation comes back.
        await DeleteScreenshotsAsync(id, ct);
        await DeletePagesAsync(id, ct);

        // Emit analytics snapshot marking session as stopped
        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: id,
            ParentSessionId: null,
            ProjectId: session.ProjectId,
            ProjectName: null,
            WorkspaceDirectory: session.Directory,
            Title: session.Title,
            Status: "deleted",
            TotalTokens: session.TotalTokens,
            TotalCost: session.TotalCost,
            TotalEstimatedCost: 0,
            MessageCount: 0,
            ModelIds: [],
            CreatedAt: DateTimeOffset.Parse(session.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            EndedAt: deletedAt,
            DurationSeconds: null,
            UserId: session.UserId));

        return Unit.Value;
    }

    /// <summary>
    /// Deletes a side conversation: its harness session (after stopping a turn it's in) and its Fleet session. The
    /// folder is its session's, so unlike <see cref="DeleteSessionAsync"/> nothing on disk is touched.
    /// </summary>
    internal async Task DiscardSideConversationAsync(Session side, CancellationToken ct)
    {
        var instance = instanceTracker.Get(side.InstanceId);
        if (instance is not null)
        {
            if (SessionActivityTracker.IsInTurn(sessionActivityTracker.GetEffectiveActivityStatus(side.Id)))
            {
                try { await instance.AbortAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { LogStopFailed(ex, side.InstanceId); }
            }

            await SafeDeleteAsync(instance, ct).ConfigureAwait(false);
            instanceTracker.Remove(side.InstanceId);
        }

        sessionRecaps?.Forget(side.Id);
        sessionNotifier?.Forget(side.Id);
        await instanceService.UpdateInstanceStatusAsync(side.InstanceId, "stopped", DateTime.UtcNow.ToString("O")).ConfigureAwait(false);
        await sessionRepository.DeleteAsync(side.Id).ConfigureAwait(false);
        LogSideConversationClosed(side.Id, side.SideOfSessionId ?? string.Empty);
    }

    private static string GetDelegationTerminalStatus(string sessionStatus) => sessionStatus switch
    {
        "error" => "error",
        _ => "completed"
    };

    private async Task SafeDeleteAsync(IHarnessSession instance, CancellationToken ct)
    {
        try { await instance.DeleteAsync(ct); }
        catch (Exception ex) { LogStopFailed(ex, instance.InstanceId); }
    }

    private static OutboxMessage CreateSessionLifecycleOutboxMessage(
        string eventType,
        string payloadJson,
        string createdAt,
        string userId)
    {
        return new OutboxMessage
        {
            Topic = "sessions",
            Type = eventType,
            Payload = payloadJson,
            UserId = userId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop instance {InstanceId}")]
    private partial void LogStopFailed(Exception ex, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end the terminals of session {SessionId}")]
    private partial void LogTerminalCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop the apps of session {SessionId}")]
    private partial void LogAppCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end what the agent left running in session {SessionId}")]
    private partial void LogArchiveInstanceFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the screenshots of session {SessionId}")]
    private partial void LogScreenshotCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the pages of session {SessionId}")]
    private partial void LogPageCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Side conversation {SideSessionId} of session {SessionId} closed")]
    private partial void LogSideConversationClosed(string sideSessionId, string sessionId);
}
