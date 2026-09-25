using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>What a side question started or went to: the side conversation, and the prompt Fleet sent it.</summary>
public sealed record SideQuestionResult(Session SideConversation, PromptSessionResult Prompt);

/// <summary>
/// Side conversations (<c>/btw</c> in the composer). A question goes to a fork of the session made at its last
/// finished turn, so the session carries on undisturbed. The fork is a Fleet session of its own, hidden from every
/// list, whose prompts carry <see cref="SideConversations.BoundaryInstruction"/>. A session has one at a time.
/// </summary>
public sealed partial class SessionOrchestrator
{
    // Static for the same reason as the activation locks: two questions sent at once must not make two forks.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SideConversationLocks = new(StringComparer.Ordinal);

    /// <summary>The side conversation open on session <paramref name="sessionId"/>, or null when there is none.</summary>
    public async Task<Result<Session?>> GetSideConversationAsync(string sessionId)
    {
        var session = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (session.IsFailure)
            return session.Error;

        return await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks <paramref name="question"/> in the session's side conversation: the open one, or a new fork of the session.
    /// The session itself isn't prompted, woken from a turn or changed.
    /// </summary>
    public async Task<Result<SideQuestionResult>> AskSideQuestionAsync(
        string sessionId,
        string? question,
        PromptOptions? options,
        string? correlationId,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        if (string.IsNullOrWhiteSpace(question))
            return FleetError.ValidationError("Session.SideQuestion", "Type a question after /btw.");
        if (question.Length > SideConversations.MaxQuestionLength)
            return FleetError.ValidationError("Session.SideQuestion", $"The question is longer than {SideConversations.MaxQuestionLength} characters.");

        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;
        if (session.SideOfSessionId is not null)
            return FleetError.ValidationError("Session.SideQuestion", "A side conversation can't have one of its own.");
        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");
        if (harnessRegistry.GetByType(session.HarnessType)?.Capabilities.SupportsSideConversations != true)
        {
            return FleetError.ValidationError(
                "Session.SideQuestion",
                $"{HarnessDisplayName(session)} sessions can't fork, so /btw isn't available here.");
        }

        var sideLock = SideConversationLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sideLock.WaitAsync(ct).ConfigureAwait(false);
        Session side;
        try
        {
            var existing = await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
            if (existing is not null)
            {
                // Asking again brings a minimized one back: the answer shows where it's asked.
                if (existing.SideMinimized)
                {
                    await sessionRepository.SetSideConversationStateAsync(existing.Id, minimized: false, discardedAt: null).ConfigureAwait(false);
                    existing.SideMinimized = false;
                }

                side = existing;
            }
            else
            {
                // A new question ends the undo window of one discarded moments ago: that fork goes now, so Undo is never
                // left to refuse because a newer one is open.
                foreach (var discarded in await sessionRepository.ListSideConversationsAsync(sessionId).ConfigureAwait(false))
                {
                    if (discarded.SideDiscardedAt is not null)
                        await DiscardSideConversationAsync(discarded, ct).ConfigureAwait(false);
                }

                var started = await StartSideConversationAsync(session, question, ct).ConfigureAwait(false);
                if (started.IsFailure)
                    return started.Error;
                side = started.Value;
            }
        }
        finally
        {
            sideLock.Release();
        }

        // The side conversation's prompts go the way every prompt does; they carry the boundary (PromptSessionCoreAsync).
        var prompt = await PromptSessionCoreAsync(
            side.Id, question, options, userMessageId: null, correlationId, saveUserMessage: false, rememberChoices: true, ct).ConfigureAwait(false);
        if (prompt.IsFailure)
            return prompt.Error;

        return new SideQuestionResult(side, prompt.Value);
    }

    /// <summary>
    /// Discards the session's side conversation: it's gone from the session at once, and deleted (in the harness and in
    /// Fleet) once <see cref="SideConversations.DiscardUndoWindow"/> has passed, unless
    /// <see cref="RestoreSideConversationAsync"/> brings it back first. Deferred on the server, so a reload or leaving
    /// the page doesn't keep a fork alive or lose one that was brought back.
    /// </summary>
    public async Task<Result<Unit>> CloseSideConversationAsync(string sessionId, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var side = await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
        if (side is null)
            return Unit.Value;

        await sessionRepository.SetSideConversationStateAsync(side.Id, side.SideMinimized, DateTime.UtcNow.ToString("O")).ConfigureAwait(false);
        LogSideConversationDiscarded(side.Id, sessionId);
        return Unit.Value;
    }

    /// <summary>
    /// Undoes the discard of the session's side conversation, within its undo window: it comes back as it was, minimized
    /// or not. Refused once a newer one is open, or when there's nothing to bring back.
    /// </summary>
    public async Task<Result<Session>> RestoreSideConversationAsync(string sessionId, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var sideLock = SideConversationLocks.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await sideLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Asking a new question deletes a discarded one first, so this is only a guard against a race between the two.
            if (await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false) is not null)
                return new FleetError("General.Conflict", "A newer side question is open. Close it to bring this one back.");

            var discarded = await sessionRepository.GetDiscardedSideConversationAsync(sessionId).ConfigureAwait(false);
            if (discarded is null)
                return FleetError.NotFoundFor("SideConversation", sessionId);

            await sessionRepository.SetSideConversationStateAsync(discarded.Id, discarded.SideMinimized, discardedAt: null).ConfigureAwait(false);
            discarded.SideDiscardedAt = null;
            return discarded;
        }
        finally
        {
            sideLock.Release();
        }
    }

    /// <summary>
    /// The session's side conversation discarded moments ago that Undo can still bring back, with how long it's still
    /// offered (<see cref="SideConversations.UndoOffered"/> from the discard), so a reload can show Undo again. Null
    /// when there's none, or its offer has run out.
    /// </summary>
    public async Task<Result<(Session SideConversation, TimeSpan UndoLeft)?>> GetUndoableSideConversationAsync(string sessionId)
    {
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var discarded = await sessionRepository.GetDiscardedSideConversationAsync(sessionId).ConfigureAwait(false);
        if (discarded is null)
            return Result.Success<(Session, TimeSpan)?>(null);

        var left = SideConversations.UndoLeft(discarded.SideDiscardedAt, DateTimeOffset.UtcNow);
        return left > TimeSpan.Zero
            ? Result.Success<(Session, TimeSpan)?>((discarded, left))
            : Result.Success<(Session, TimeSpan)?>(null);
    }

    /// <summary>
    /// Records <paramref name="answerId"/> as the newest answer of the session's side conversation the user has seen, with
    /// its panel open. A newer finished answer is then news, after a reload too.
    /// </summary>
    public async Task<Result<Session>> SetSideConversationSeenAsync(string sessionId, string? answerId)
    {
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var side = await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
        if (side is null)
            return FleetError.NotFoundFor("SideConversation", sessionId);

        await sessionRepository.SetSideSeenAnswerAsync(side.Id, answerId).ConfigureAwait(false);
        side.SideSeenAnswerId = answerId;
        return side;
    }

    /// <summary>
    /// Folds the session's side conversation into its tab on the composer, or opens it again. Kept with it, so a reload
    /// or coming back to the session finds it as it was left.
    /// </summary>
    public async Task<Result<Session>> SetSideConversationMinimizedAsync(string sessionId, bool minimized, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var side = await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
        if (side is null)
            return FleetError.NotFoundFor("SideConversation", sessionId);

        await sessionRepository.SetSideConversationStateAsync(side.Id, minimized, discardedAt: null).ConfigureAwait(false);
        side.SideMinimized = minimized;
        return side;
    }

    /// <summary>
    /// Deletes side conversation <paramref name="sideSessionId"/> if it's still discarded: its undo window has passed.
    /// Called by the sweeper, as the side conversation's owner.
    /// </summary>
    public async Task DeleteDiscardedSideConversationAsync(string sideSessionId, CancellationToken ct = default)
    {
        var side = await sessionRepository.GetByIdAsync(sideSessionId).ConfigureAwait(false);
        if (side is not { SideOfSessionId: not null, SideDiscardedAt: not null })
            return;

        await DiscardSideConversationAsync(side, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the session's side conversation as a session of its own: it's listed like any other, and its prompts
    /// carry <see cref="SideConversations.KeptNotice"/> instead of the boundary.
    /// </summary>
    public async Task<Result<Session>> KeepSideConversationAsync(string sessionId, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var side = await sessionRepository.GetSideConversationAsync(sessionId).ConfigureAwait(false);
        if (side is null)
            return FleetError.NotFoundFor("SideConversation", sessionId);

        // A workspace of its own over the same folder, as a fork gets: deleting either session then leaves the
        // other's folder alone (only the workspace a session was made with is cleaned up).
        var directory = await workspaceService.GetWorkspaceDirectoryAsync(side.WorkspaceId).ConfigureAwait(false);
        if (directory.IsFailure)
            return directory.Error;
        var workspace = await workspaceService.CreateWorkspaceAsync(directory.Value, "existing").ConfigureAwait(false);
        if (workspace.IsFailure)
            return workspace.Error;

        await sessionRepository.KeepSideConversationAsync(side.Id, workspace.Value.Id).ConfigureAwait(false);
        side.WorkspaceId = workspace.Value.Id;
        side.SideOfSessionId = null;
        side.IsHidden = false;
        side.KeptFromSide = true;

        // It shows up in the list now, as a new session would.
        await eventBroadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new SessionCreatedOutboxPayload
            {
                SessionId = side.Id,
                InstanceId = side.InstanceId,
                WorkspaceId = side.WorkspaceId,
                Title = side.Title,
                ProjectId = side.ProjectId,
            }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
            side.UserId, ct).ConfigureAwait(false);

        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: side.Id,
            ParentSessionId: null,
            ProjectId: side.ProjectId,
            ProjectName: await ResolveProjectNameAsync(side.ProjectId).ConfigureAwait(false),
            WorkspaceDirectory: side.Directory,
            Title: side.Title,
            Status: "active",
            TotalTokens: side.TotalTokens,
            TotalCost: side.TotalCost,
            TotalEstimatedCost: 0,
            MessageCount: 0,
            ModelIds: [],
            CreatedAt: DateTimeOffset.UtcNow,
            EndedAt: null,
            DurationSeconds: null,
            UserId: side.UserId));

        return side;
    }

    /// <summary>
    /// Forks <paramref name="session"/> at its last finished turn and makes the fork a hidden Fleet session beside it,
    /// on the harness process the session runs on.
    /// </summary>
    private async Task<Result<Session>> StartSideConversationAsync(Session session, string question, CancellationToken ct)
    {
        var runtime = harnessRegistry.GetRuntimeByType(session.HarnessType);
        if (runtime is null)
            return FleetError.NotFoundFor("HarnessRuntime", session.HarnessType);

        var directory = await workspaceService.GetWorkspaceDirectoryAsync(session.WorkspaceId).ConfigureAwait(false);
        if (directory.IsFailure)
            return directory.Error;

        var instance = await GetOrActivateInstanceAsync(session, ct).ConfigureAwait(false);
        if (instance.IsFailure)
            return instance.Error;

        SideConversationFork? fork;
        try
        {
            fork = await instance.Value.ForkSideConversationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSideConversationForkFailed(ex, session.Id);
            return new FleetError("Session.SideQuestionFailed", "Fleet couldn't fork the session for /btw. Fleet's log has the details.");
        }

        if (fork is null)
            return FleetError.ValidationError("Session.SideQuestion", $"{HarnessDisplayName(session)} couldn't fork this session.");

        // Prepared as the session is, so the fork runs on the same harness process: a pooled process, or a V2 server,
        // holds its sessions for the launch it was given.
        var profile = await ResolveSessionProfileAsync(session.HarnessProfileId).ConfigureAwait(false);
        if (profile.IsFailure)
            return profile.Error;

        var credentials = await credentialStore.GetDecryptedCredentialsAsync(session.UserId).ConfigureAwait(false);
        var preparation = await runtime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = session.UserId,
            UserCredentials = credentials,
            ModelId = null,
            WorkingDirectory = directory.Value,
            Profile = profile.Value,
        }, ct).ConfigureAwait(false);
        if (preparation is RuntimePreparation.NotReady notReady)
            return FleetError.ValidationError("Session.NotReady", string.Join(" ", notReady.Errors.Select(e => e.Message)));

        var sideId = Guid.NewGuid().ToString();
        IHarnessSession sideInstance;
        try
        {
            sideInstance = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = sideId,
                WorkingDirectory = directory.Value,
                OwnerUserId = session.UserId,
                ResumeToken = fork.ResumeToken,
                ProjectId = session.ProjectId,
                ProjectName = await ResolveProjectNameAsync(session.ProjectId).ConfigureAwait(false),
                LaunchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts,
                ParentSessionId = session.Id,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSideConversationForkFailed(ex, session.Id);
            return new FleetError("Session.SideQuestionFailed", "Fleet couldn't start the side conversation. Fleet's log has the details.");
        }

        var registered = await instanceService.RegisterInstanceAsync(
            id: sideInstance.InstanceId,
            port: 0,
            pid: sideInstance.ProcessId,
            directory: directory.Value,
            url: string.Empty).ConfigureAwait(false);
        if (registered.IsFailure)
        {
            await SafeDeleteAsync(sideInstance, ct).ConfigureAwait(false);
            return registered.Error;
        }

        var side = new Session
        {
            Id = sideId,
            WorkspaceId = session.WorkspaceId,
            InstanceId = sideInstance.InstanceId,
            ProjectId = session.ProjectId,
            OpencodeSessionId = fork.ResumeToken,
            Title = SideConversations.Title(question),
            Status = "active",
            ActivityStatus = _activityStatusIdle,
            Directory = session.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            LifecycleStatus = _lifecycleStatusRunning,
            HarnessType = session.HarnessType,
            RuntimeMode = session.RuntimeMode,
            HarnessProfileId = session.HarnessProfileId,
            HarnessResumeToken = fork.ResumeToken,
            IsHidden = true,
            UserId = session.UserId,
            // The session's own choices, so a question that names none is asked as the session's prompts are: the
            // same model and agent read the copied conversation from the provider's cache.
            SelectedAgent = session.SelectedAgent,
            SelectedProviderId = session.SelectedProviderId,
            SelectedModelId = session.SelectedModelId,
            SideOfSessionId = session.Id,
            SideBoundaryMessageId = fork.BoundaryMessageId,
        };

        try
        {
            // Before the instance is registered: registration starts the relay pump, which finds the session in the
            // database. No session_created: nothing lists a side conversation.
            await sessionRepository.InsertAsync(side).ConfigureAwait(false);
        }
        catch
        {
            await SafeDeleteAsync(sideInstance, ct).ConfigureAwait(false);
            throw;
        }

        instanceTracker.Register(sideInstance.InstanceId, sideInstance);
        LogSideConversationStarted(side.Id, session.Id, fork.BoundaryMessageId);
        return side;
    }

    /// <summary>
    /// Deletes a side conversation: its harness session (after stopping a turn it's in) and its Fleet session. The
    /// folder is its session's, so unlike <see cref="DeleteSessionAsync"/> nothing on disk is touched.
    /// </summary>
    private async Task DiscardSideConversationAsync(Session side, CancellationToken ct)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't fork session {SessionId} for a side conversation")]
    private partial void LogSideConversationForkFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Side conversation {SideSessionId} started on session {SessionId} after message {BoundaryMessageId}")]
    private partial void LogSideConversationStarted(string sideSessionId, string sessionId, string? boundaryMessageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Side conversation {SideSessionId} of session {SessionId} discarded; deleted once its undo window has passed")]
    private partial void LogSideConversationDiscarded(string sideSessionId, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Side conversation {SideSessionId} of session {SessionId} closed")]
    private partial void LogSideConversationClosed(string sideSessionId, string sessionId);
}
