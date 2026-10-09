using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Activation;

/// <summary>
/// Waking a session whose harness isn't running, and what every harness instance needs before Fleet talks to it: the
/// session's profile as it is now, the permission level, and an event subscription that's up. A wake resumes the
/// harness session with its token (or starts a fresh one when the session was never prompted), registers the instance,
/// and says the session is running and idle; a wake that fails marks the session errored.
/// </summary>
public sealed partial class SessionActivation(
    WorkspaceService workspaceService,
    InstanceService instanceService,
    IHarnessRegistry harnessRegistry,
    InstanceTracker instanceTracker,
    ISessionRepository sessionRepository,
    IProjectRepository projectRepository,
    IEventBroadcaster eventBroadcaster,
    ICredentialStore credentialStore,
    IUserPreferenceRepository userPreferenceRepository,
    ILogger<SessionActivation> logger,
    IHarnessProfileRepository? harnessProfiles = null,
    SessionNotifier? sessionNotifier = null) : ISessionActivator
{
    // Static because the session services are scoped: opening a session wakes it from several requests at once, and a
    // per-request lock let each of them start its own harness for the same session.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ActivationLocks = new(StringComparer.Ordinal);

    private const string _lifecycleStatusRunning = "running";
    private const string _lifecycleStatusError = "error";
    private const string _activityStatusIdle = "idle";

    /// <inheritdoc />
    public async Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var _ = logger.BeginSessionScope(sessionId);
        var sessionResult = await sessionRepository.GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        return await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The session's running harness, or the session woken: resumed with its token, or started fresh when it was never
    /// prompted. One wake per session at a time, however many requests ask at once.
    /// </summary>
    public async Task<Result<IHarnessSession>> GetOrActivateInstanceAsync(Session session, CancellationToken ct)
    {
        using var activateActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.activate_instance",
            ActivityKind.Internal);
        activateActivity?.SetTag(FleetInstrumentation.SessionIdTag, session.Id);

        var instance = instanceTracker.Get(session.InstanceId);
        if (instance is not null)
            return Result.Success<IHarnessSession>(instance);

        var activationLock = ActivationLocks.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
        await activationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentSession = await sessionRepository.GetByIdAsync(session.Id).ConfigureAwait(false);
            if (currentSession is null)
                return FleetError.NotFoundFor(nameof(Session), session.Id);

            instance = instanceTracker.Get(currentSession.InstanceId);
            if (instance is not null)
                return Result.Success<IHarnessSession>(instance);

            var activatedResult = await ActivateSessionAsync(currentSession, ct).ConfigureAwait(false);
            return activatedResult;
        }
        finally
        {
            activationLock.Release();
        }
    }

    /// <summary>The profile an existing session started with, as it is now.</summary>
    public async Task<Result<HarnessProfile?>> ResolveSessionProfileAsync(string? profileId)
    {
        if (profileId is null)
            return Result.Success<HarnessProfile?>(null);

        var profile = harnessProfiles is null ? null : await harnessProfiles.GetByIdAsync(profileId).ConfigureAwait(false);
        if (profile is null)
            return FleetError.ValidationError("Session.Profile", "The profile this session started with has been deleted. Start a new session to go on.");
        return profile;
    }

    private async Task<Result<IHarnessSession>> ActivateSessionAsync(Session session, CancellationToken ct)
    {
        var workspaceResult = await workspaceService.GetWorkspaceDirectoryAsync(session.WorkspaceId).ConfigureAwait(false);
        if (workspaceResult.IsFailure)
        {
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return workspaceResult.Error;
        }

        var harnessRuntime = harnessRegistry.GetRuntimeByType(session.HarnessType);
        if (harnessRuntime is null)
        {
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return FleetError.NotFoundFor("HarnessRuntime", session.HarnessType);
        }

        // The session wakes with its profile as it is now, so an edited profile reaches it here.
        var profile = await ResolveSessionProfileAsync(session.HarnessProfileId).ConfigureAwait(false);
        if (profile.IsFailure)
        {
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return profile.Error;
        }

        var ownerCredentials = await credentialStore.GetDecryptedCredentialsAsync(session.UserId).ConfigureAwait(false);
        var preparation = await harnessRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = session.UserId,
            UserCredentials = ownerCredentials,
            ModelId = null,
            WorkingDirectory = workspaceResult.Value,
            Profile = profile.Value
        }, ct).ConfigureAwait(false);

        if (preparation is RuntimePreparation.NotReady notReady)
        {
            var message = string.Join(" ", notReady.Errors.Select(e => e.Message));
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return FleetError.ValidationError("Session.NotReady", message);
        }

        var launchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts;
        var projectName = await ResolveProjectNameAsync(session.ProjectId).ConfigureAwait(false);

        IHarnessSession harnessInstance;
        try
        {
            // Non-pooled sessions only get a resume token on their first prompt, so one that was
            // never prompted has nothing to resume: start a fresh harness session instead.
            harnessInstance = string.IsNullOrWhiteSpace(session.HarnessResumeToken)
                ? await harnessRuntime.SpawnAsync(new HarnessSpawnOptions
                {
                    SessionId = session.Id,
                    WorkingDirectory = workspaceResult.Value,
                    OwnerUserId = session.UserId,
                    ProjectId = session.ProjectId,
                    ProjectName = projectName,
                    LaunchArtifacts = launchArtifacts,
                    WorkflowStep = session.WorkflowRunId is not null && !session.WorkflowUserFinishes,
                }, ct).ConfigureAwait(false)
                : await harnessRuntime.ResumeAsync(new HarnessResumeOptions
                {
                    SessionId = session.Id,
                    WorkingDirectory = workspaceResult.Value,
                    OwnerUserId = session.UserId,
                    ResumeToken = session.HarnessResumeToken,
                    ProjectId = session.ProjectId,
                    ProjectName = projectName,
                    LaunchArtifacts = launchArtifacts,
                    WorkflowStep = session.WorkflowRunId is not null && !session.WorkflowUserFinishes,
                    DelegatedChild = session.ParentSessionId is not null,
                }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogAutomaticActivationFailed(ex, session.Id, session.HarnessType);
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return new FleetError("Session.ActivationFailed", CreateAutomaticActivationFailedMessage(ex));
        }

        var instanceResult = await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,
            pid: harnessInstance.ProcessId,
            directory: workspaceResult.Value,
            url: string.Empty).ConfigureAwait(false);
        if (instanceResult.IsFailure)
        {
            await SafeStopAsync(harnessInstance, ct).ConfigureAwait(false);
            await MarkAutomaticActivationErrorAsync(session, ct).ConfigureAwait(false);
            return instanceResult.Error;
        }

        // Update the DB mapping BEFORE registering: registration starts the relay pump, which
        // resolves the Fleet session id by instance id from the DB.
        await sessionRepository.UpdateForResumeAsync(session.Id, harnessInstance.InstanceId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.HarnessResumeToken) && !string.IsNullOrWhiteSpace(harnessInstance.ResumeToken))
        {
            // A fresh pooled spawn creates its OpenCode session up front; keep its token so the next wake resumes it.
            await sessionRepository.UpdateResumeTokenAsync(session.Id, harnessInstance.ResumeToken).ConfigureAwait(false);
            session.HarnessResumeToken = harnessInstance.ResumeToken;
        }

        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
        session.InstanceId = harnessInstance.InstanceId;
        session.Status = "active";
        session.LifecycleStatus = _lifecycleStatusRunning;
        session.ActivityStatus = _activityStatusIdle;
        session.StoppedAt = null;
        await BroadcastAutomaticActivationStatusAsync(session, _activityStatusIdle, _lifecycleStatusRunning, ct).ConfigureAwait(false);

        // Before anything reaches it: a woken subagent, or a turn resumed on the harness, asks as the settings say.
        await ApplyPermissionsAsync(session, harnessInstance, ct).ConfigureAwait(false);

        return Result.Success<IHarnessSession>(harnessInstance);
    }

    /// <summary>
    /// Tells the session's harness what it may do without asking (Settings → Permissions). Called before every prompt, so
    /// a changed setting applies from the next message. When the harness can't be told, the prompt still goes: it keeps
    /// the level it had.
    /// </summary>
    public async Task ApplyPermissionsAsync(Session session, IHarnessSession instance, CancellationToken ct)
    {
        try
        {
            var policy = await SessionPermissions.ResolveAsync(userPreferenceRepository, sessionRepository, session).ConfigureAwait(false);
            await instance.ApplyPermissionsAsync(policy, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPermissionsNotApplied(ex, session.Id);
        }
    }

    private async Task MarkAutomaticActivationErrorAsync(Session session, CancellationToken ct)
    {
        await sessionRepository.UpdateStatusAsync(session.Id, _lifecycleStatusError).ConfigureAwait(false);
        session.Status = _lifecycleStatusError;
        session.LifecycleStatus = _lifecycleStatusError;
        session.ActivityStatus = _activityStatusIdle;
        await BroadcastAutomaticActivationStatusAsync(session, _lifecycleStatusError, _lifecycleStatusError, ct).ConfigureAwait(false);
        sessionNotifier?.OnSessionFailed(session.Id, "It couldn't start.");
    }

    private async Task BroadcastAutomaticActivationStatusAsync(
        Session session,
        string activityStatus,
        string lifecycleStatus,
        CancellationToken ct)
    {
        var capabilities = ResolveCurrentCapabilities(session, activityStatus, lifecycleStatus);
        var sessionStatusPayload = JsonSerializer.SerializeToElement(
            new SessionStatusBroadcastPayload(
                session.Id,
                new SessionStatusBroadcastState(activityStatus),
                lifecycleStatus,
                capabilities),
            ApplicationJsonContext.Default.SessionStatusBroadcastPayload);

        await eventBroadcaster.BroadcastAsync(
            $"session:{session.Id}",
            EventTypes.SessionStatus,
            sessionStatusPayload,
            session.UserId,
            ct).ConfigureAwait(false);

        var activityStatusPayload = JsonSerializer.SerializeToElement(
            new ActivityStatusBroadcastPayload(session.Id, activityStatus, capabilities),
            ApplicationJsonContext.Default.ActivityStatusBroadcastPayload);

        await eventBroadcaster.BroadcastAsync(
            "sessions",
            "activity_status",
            activityStatusPayload,
            session.UserId,
            ct).ConfigureAwait(false);
    }

    private SessionActionCapabilities ResolveCurrentCapabilities(
        Session session,
        string activityStatus,
        string lifecycleStatus)
    {
        var harness = harnessRegistry.GetByType(session.HarnessType);
        return SessionCapabilitiesResolver.Resolve(
            lifecycleStatus,
            session.RetentionStatus,
            activityStatus,
            instanceTracker.Get(session.InstanceId) is not null,
            SessionCapabilitiesResolver.ForkUnsupportedReason(harness),
            SessionCapabilitiesResolver.PromptUnsupportedReason(session, harness),
            SessionCapabilitiesResolver.CompactUnsupportedReason(harness));
    }

    private static string CreateAutomaticActivationFailedMessage(Exception exception)
    {
        var baseException = exception.GetBaseException();
        var message = string.IsNullOrWhiteSpace(baseException.Message)
            ? exception.Message
            : baseException.Message;

        return string.IsNullOrWhiteSpace(message)
            ? "Automatic session activation failed."
            : $"Automatic session activation failed: {message}";
    }

    /// <summary>
    /// Waits for the harness event subscription to be established before proceeding.
    /// This ensures events emitted immediately after activation/resume are not lost.
    /// Times out after 5 seconds and proceeds with a warning rather than failing the operation.
    /// </summary>
    public async Task EnsureEventSubscriptionReadyAsync(
        IHarnessSession instance,
        string sessionId,
        CancellationToken ct)
    {
        const int timeoutMs = 5000;

        using var subActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.ensure_subscription",
            ActivityKind.Internal);
        subActivity?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await instance.WaitForEventSubscriptionAsync(timeoutCts.Token).ConfigureAwait(false);

            subActivity?.SetTag("subscription.ready", true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — log warning and proceed rather than failing the prompt.
            subActivity?.SetTag("subscription.ready", false);
            subActivity?.SetTag("subscription.timeout_ms", timeoutMs);

            LogSubscriptionReadinessTimeout(sessionId, timeoutMs);
        }
    }


    private async Task<string?> ResolveProjectNameAsync(string? projectId)
    {
        if (projectId is null)
            return null;

        var projects = await projectRepository.ListAsync();
        return projects.FirstOrDefault(p => p.Id == projectId)?.Name;
    }

    private async Task SafeStopAsync(IHarnessSession instance, CancellationToken ct)
    {
        try { await instance.StopAsync(ct); }
        catch (Exception ex) { LogStopFailed(ex, instance.InstanceId); }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop instance {InstanceId}")]
    private partial void LogStopFailed(Exception ex, string instanceId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to automatically activate session {SessionId} for harness {HarnessType}")]
    private partial void LogAutomaticActivationFailed(Exception ex, string sessionId, string harnessType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Event subscription readiness timed out for session {SessionId} after {TimeoutMs}ms — proceeding with prompt")]
    private partial void LogSubscriptionReadinessTimeout(string sessionId, int timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Couldn't tell session {SessionId}'s harness its permission level; it keeps the one it had")]
    private partial void LogPermissionsNotApplied(Exception ex, string sessionId);
}
