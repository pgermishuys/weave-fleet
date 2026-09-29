using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// A session's harness session forked into a new one (<see cref="IHarnessSession.ForkConversationAsync"/>) and attached
/// to the Fleet session <paramref name="Instance"/> runs as.
/// </summary>
internal sealed record ForkedHarnessSession(ConversationFork Fork, IHarnessSession Instance, string Directory);

/// <summary>
/// Fork: a new session holding a copy of a session's conversation up to its last finished turn, in the same folder, on
/// the same harness, profile, agent and model. From there the two go their own ways.
/// </summary>
public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Forks session <paramref name="parentId"/>: its harness copies the conversation up to the last finished turn, and
    /// the copy becomes a session of its own, titled "Fork of …" unless <paramref name="title"/> is given. Refused on a
    /// harness that can't copy a conversation (<see cref="HarnessCapabilities.SupportsForking"/>).
    /// </summary>
    public async Task<Result<CreateSessionResult>> ForkSessionAsync(
        string parentId,
        string? title = null,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(parentId);
        var parentResult = await GetSessionAsync(parentId).ConfigureAwait(false);
        if (parentResult.IsFailure)
            return parentResult.Error;

        var parent = parentResult.Value;
        if (SessionCapabilitiesResolver.ForkUnsupportedReason(harnessRegistry.GetByType(parent.HarnessType)) is { } reason)
            return FleetError.ValidationError("Session.Fork", reason);

        var forkId = Guid.NewGuid().ToString();
        var forked = await ForkHarnessSessionAsync(parent, forkId, ct).ConfigureAwait(false);
        if (forked.IsFailure)
            return forked.Error;

        var (fork, instance, directory) = forked.Value;

        // A workspace of its own over the same folder, as a kept side conversation gets: deleting either session then
        // leaves the other's folder alone (only the workspace a session was made with is cleaned up).
        var workspace = await workspaceService.CreateWorkspaceAsync(directory, "existing").ConfigureAwait(false);
        if (workspace.IsFailure)
        {
            await SafeDeleteAsync(instance, ct).ConfigureAwait(false);
            return workspace.Error;
        }

        var gitBaseline = await _gitDiffService.CaptureBaselineAsync(directory, forkId, ct).ConfigureAwait(false);
        var session = new Session
        {
            Id = forkId,
            WorkspaceId = workspace.Value.Id,
            InstanceId = instance.InstanceId,
            ProjectId = parent.ProjectId,
            OpencodeSessionId = fork.ResumeToken,
            Title = string.IsNullOrWhiteSpace(title) ? $"Fork of {parent.Title}" : title.Trim(),
            Status = "active",
            ActivityStatus = _activityStatusIdle,
            Directory = parent.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            LifecycleStatus = _lifecycleStatusRunning,
            HarnessType = parent.HarnessType,
            RuntimeMode = parent.RuntimeMode,
            HarnessProfileId = parent.HarnessProfileId,
            HarnessResumeToken = fork.ResumeToken,
            GitBaselineRef = gitBaseline?.RefName,
            GitRepoRoot = gitBaseline?.RepoRoot,
            UserId = parent.UserId,
            // The copied conversation carries on as the parent's did.
            SelectedAgent = parent.SelectedAgent,
            SelectedProviderId = parent.SelectedProviderId,
            SelectedModelId = parent.SelectedModelId,
        };

        try
        {
            // Before the instance is registered: registration starts the relay pump, which finds the session in the
            // database.
            await sessionRepository.InsertAsync(session).ConfigureAwait(false);
        }
        catch
        {
            await SafeDeleteAsync(instance, ct).ConfigureAwait(false);
            throw;
        }

        instanceTracker.Register(instance.InstanceId, instance);
        LogSessionForked(session.Id, parent.Id, fork.BoundaryMessageId);

        await eventBroadcaster.BroadcastAsync("sessions", "session_created",
            JsonSerializer.SerializeToElement(new SessionCreatedOutboxPayload
            {
                SessionId = session.Id,
                InstanceId = session.InstanceId,
                WorkspaceId = session.WorkspaceId,
                Title = session.Title,
                ProjectId = session.ProjectId,
            }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
            session.UserId, ct).ConfigureAwait(false);

        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: session.Id,
            ParentSessionId: null,
            ProjectId: session.ProjectId,
            ProjectName: await ResolveProjectNameAsync(session.ProjectId).ConfigureAwait(false),
            WorkspaceDirectory: session.Directory,
            Title: session.Title,
            Status: "active",
            TotalTokens: 0,
            TotalCost: 0,
            TotalEstimatedCost: 0,
            MessageCount: 0,
            ModelIds: [],
            CreatedAt: DateTimeOffset.UtcNow,
            EndedAt: null,
            DurationSeconds: null,
            UserId: session.UserId));

        return new CreateSessionResult(session, instance.InstanceId, workspace.Value.Id, workspace.Value.Branch);
    }

    /// <summary>
    /// Forks <paramref name="session"/>'s harness session at its last finished turn and attaches the copy as Fleet session
    /// <paramref name="forkSessionId"/>, on the harness process the session runs on. The instance isn't registered with
    /// the tracker: the caller does that once the Fleet session is saved.
    /// </summary>
    private async Task<Result<ForkedHarnessSession>> ForkHarnessSessionAsync(Session session, string forkSessionId, CancellationToken ct)
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

        ConversationFork? fork;
        try
        {
            fork = await instance.Value.ForkConversationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogForkFailed(ex, session.Id);
            return new FleetError("Session.ForkFailed", "Fleet couldn't fork the session. Fleet's log has the details.");
        }

        if (fork is null)
            return FleetError.ValidationError("Session.Fork", $"{HarnessDisplayName(session)} couldn't fork this session.");

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

        IHarnessSession forkInstance;
        try
        {
            forkInstance = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = forkSessionId,
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
            LogForkFailed(ex, session.Id);
            return new FleetError("Session.ForkFailed", "Fleet couldn't start the forked session. Fleet's log has the details.");
        }

        var registered = await instanceService.RegisterInstanceAsync(
            id: forkInstance.InstanceId,
            port: 0,
            pid: forkInstance.ProcessId,
            directory: directory.Value,
            url: string.Empty).ConfigureAwait(false);
        if (registered.IsFailure)
        {
            await SafeDeleteAsync(forkInstance, ct).ConfigureAwait(false);
            return registered.Error;
        }

        return new ForkedHarnessSession(fork, forkInstance, directory.Value);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't fork session {SessionId}")]
    private partial void LogForkFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {ForkSessionId} forked from session {SessionId} after message {BoundaryMessageId}")]
    private partial void LogSessionForked(string forkSessionId, string sessionId, string? boundaryMessageId);
}
