using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// High-level coordinator for session lifecycle operations.
/// Bridges workspace creation, harness spawning, DB persistence, and harness communication.
/// </summary>
public sealed partial class SessionOrchestrator(
    WorkspaceService workspaceService,
    InstanceService instanceService,
    SessionSourceResolutionService sessionSourceResolutionService,
    IHarnessRegistry harnessRegistry,
    InstanceTracker instanceTracker,
    ISessionRepository sessionRepository,
    ISessionSourceUsageRepository sessionSourceUsageRepository,
    ISessionCallbackRepository sessionCallbackRepository,
    IDelegationRepository delegationRepository,
    IProjectRepository projectRepository,
    IEventBroadcaster eventBroadcaster,
    IAnalyticsCollector analyticsCollector,
    IMessageRepository messageRepository,
    IOutboxRepository outboxRepository,
    DelegationService delegationService,
    ICredentialStore credentialStore,
    IUserContext userContext,
    FleetOptions options,
    ILogger<SessionOrchestrator> logger,
    SessionActivityWriteService? sessionActivityWriteService = null)
{
    private readonly DelegationService _delegationService = delegationService;

    private sealed class NoOpOutboxRepository : IOutboxRepository
    {
        public Task<long> EnqueueAsync(OutboxMessage message) => Task.FromResult(0L);

        public Task<long> EnqueueAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, OutboxMessage message)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<OutboxMessage>> GetUndispatchedAsync(int limit)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public Task<IReadOnlyList<OutboxMessage>> GetByTopicAfterAsync(string topic, long sequenceNumber, int limit)
            => Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public Task MarkDispatchedAsync(IReadOnlyList<long> ids, string dispatchedAt) => Task.CompletedTask;

        public Task<int> DeleteDispatchedBeforeAsync(string dispatchedBefore, int limit) => Task.FromResult(0);
    }

    public SessionOrchestrator(
        WorkspaceService workspaceService,
        InstanceService instanceService,
        SessionSourceResolutionService sessionSourceResolutionService,
        IHarnessRegistry harnessRegistry,
        InstanceTracker instanceTracker,
        ISessionRepository sessionRepository,
        ISessionSourceUsageRepository sessionSourceUsageRepository,
        ISessionCallbackRepository sessionCallbackRepository,
        IDelegationRepository delegationRepository,
        IProjectRepository projectRepository,
        IEventBroadcaster eventBroadcaster,
        IAnalyticsCollector analyticsCollector,
        IMessageRepository messageRepository,
        DelegationService delegationService,
        ICredentialStore credentialStore,
        IUserContext userContext,
        FleetOptions options,
        ILogger<SessionOrchestrator> logger)
        : this(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            harnessRegistry,
            instanceTracker,
            sessionRepository,
            sessionSourceUsageRepository,
            sessionCallbackRepository,
            delegationRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            messageRepository,
            new NoOpOutboxRepository(),
            delegationService,
            credentialStore,
            userContext,
            options,
            logger,
            sessionActivityWriteService: null)
    {
    }

    private const string DefaultHarnessType = "opencode";
    private const string ScratchProjectName = "Scratch";

    // ── Create ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full create-session flow:
    /// 1. Create or reuse workspace
    /// 2. Spawn harness instance
    /// 3. Persist instance + session records
    /// 4. Optionally register a completion callback
    /// </summary>
    public async Task<Result<CreateSessionResult>> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken ct = default)
    {
        // Cloud mode: reject caller-supplied Directory to prevent arbitrary path traversal.
        // Internal requests (e.g. fork) are exempt since their Directory comes from a trusted managed path.
        if (options.Cloud.Enabled && !request.IsInternalRequest && !string.IsNullOrWhiteSpace(request.Directory))
        {
            return FleetError.ValidationError(
                nameof(CreateSessionRequest.Directory),
                "Arbitrary directory paths are not allowed in cloud mode. Managed workspaces are created automatically.");
        }

        var sourceResolutionResult = await sessionSourceResolutionService.ResolveCreateRequestAsync(request, ct);
        if (sourceResolutionResult.IsFailure)
            return sourceResolutionResult.Error;

        var workspaceIntent = sourceResolutionResult.Value.Input.WorkspaceIntent;
        if (workspaceIntent is null)
            return FleetError.ValidationError(
                "SessionSource.WorkspaceIntent",
                "The selected session source cannot start a workspace-backed session.");

        // Resolve harness
        var harnessType = request.HarnessType ?? DefaultHarnessType;
        var harness = harnessRegistry.GetByType(harnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);

        var harnessRuntime = harnessRegistry.GetRuntimeByType(harnessType);
        if (harnessRuntime is null)
            return FleetError.NotFoundFor("HarnessRuntime", harnessType);

        // Prepare runtime: load user credentials and call harness preparation pipeline.
        // The orchestrator passes the opaque credential bag to the harness — it does not
        // inspect, interpret, or filter the credentials itself.
        var userCredentials = await credentialStore.GetDecryptedCredentialsAsync(userContext.UserId);
        var preparation = await harnessRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = userContext.UserId,
            UserCredentials = userCredentials,
            ModelId = null, // model selection happens inside the session, not at creation time
            WorkingDirectory = workspaceIntent.Directory
        }, ct);

        if (preparation is RuntimePreparation.NotReady notReady)
        {
            var message = string.Join(" ", notReady.Errors.Select(e => e.Message));
            return FleetError.ValidationError("Session.NotReady", message);
        }

        var launchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts;

        // Resolve or default project
        var projectId = request.ProjectId ?? await ResolveScratchProjectIdAsync();

        // Look up project name for analytics context (best-effort)
        string? projectName = null;
        if (projectId is not null)
        {
            var projects = await projectRepository.ListAsync();
            projectName = projects.FirstOrDefault(p => p.Id == projectId)?.Name;
        }

        // 1. Create workspace
        var workspaceResult = await workspaceService.CreateWorkspaceAsync(
            workspaceIntent.Directory,
            workspaceIntent.IsolationStrategy,
            workspaceIntent.Branch,
            sourceResolutionResult.Value.Input.Provenance);
        if (workspaceResult.IsFailure)
            return workspaceResult.Error;

        var workspace = workspaceResult.Value;

        // 2. Spawn harness instance
        var sessionId = Guid.NewGuid().ToString();
        IHarnessSession harnessInstance;
        try
        {
            harnessInstance = await harnessRuntime.SpawnAsync(new HarnessSpawnOptions
            {
                SessionId = sessionId,
                WorkingDirectory = workspace.Directory,
                OwnerUserId = userContext.UserId,
                InitialPrompt = request.InitialPrompt,
                Branch = workspaceIntent.Branch,
                ProjectId = projectId,
                ProjectName = projectName,
                LaunchArtifacts = launchArtifacts
            }, ct);
        }
        catch (Exception ex)
        {
            LogSpawnFailed(ex, harnessType);
            return FleetError.Unexpected;
        }

        // 3. Persist instance
        var instanceResult = await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,           // port is harness-implementation detail; 0 = unknown
            pid: harnessInstance.ProcessId,
            directory: workspace.Directory,
            url: string.Empty);
        if (instanceResult.IsFailure)
        {
            await SafeStopAsync(harnessInstance, ct);
            return instanceResult.Error;
        }

        // Track in-memory handle
        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);

        // 4. Persist session
        var session = new Session
        {
            Id = sessionId,
            WorkspaceId = workspace.Id,
            InstanceId = harnessInstance.InstanceId,
            ProjectId = projectId,
            OpencodeSessionId = harnessInstance.InstanceId,
            Title = request.Title ?? "Untitled",
            Status = "active",
            Directory = workspace.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            HarnessType = harnessType,
            UserId = userContext.UserId
        };

        var createdAt = DateTime.UtcNow.ToString("O");
        session.CreatedAt = createdAt;
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.InsertAsync(session);
            await eventBroadcaster.BroadcastAsync("sessions", "session_created", new
            {
                sessionId = session.Id,
                instanceId = harnessInstance.InstanceId,
                workspaceId = workspace.Id,
                title = session.Title,
                projectId = session.ProjectId
            }, userContext.UserId, ct);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    SessionsToInsert = [session],
                    OutboxMessages =
                    [
                        CreateSessionLifecycleOutboxMessage(
                            "session_created",
                            new
                            {
                                sessionId = session.Id,
                                instanceId = harnessInstance.InstanceId,
                                workspaceId = workspace.Id,
                                title = session.Title,
                                projectId = session.ProjectId
                            },
                            createdAt,
                            userContext.UserId)
                    ]
                },
                ct);
        }

        await sessionSourceUsageRepository.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = session.Id,
            WorkspaceId = workspace.Id,
            ProviderId = sourceResolutionResult.Value.Input.Provenance.ProviderId,
            SourceType = sourceResolutionResult.Value.Input.Provenance.SourceType,
            ActionId = sourceResolutionResult.Value.Input.Provenance.ActionId,
            ResourceId = sourceResolutionResult.Value.Input.Provenance.ResourceId,
            ResourceUrl = sourceResolutionResult.Value.Input.Provenance.ResourceUrl,
            Title = sourceResolutionResult.Value.Input.Provenance.Title,
            Summary = sourceResolutionResult.Value.Input.Provenance.Summary,
            CreatedAt = createdAt
        });
        LogSessionCreated(session.Id, workspace.Id, harnessInstance.InstanceId);

        // Emit analytics snapshot for the new session
        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: session.Id,
            ParentSessionId: null,
            ProjectId: projectId,
            ProjectName: projectName,
            WorkspaceDirectory: workspace.Directory,
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
            UserId: userContext.UserId));

        // 5. Register callback (optional)
        if (request.OnCompleteTargetSessionId is not null && request.OnCompleteTargetInstanceId is not null)
        {
            // Ownership guard: target session must belong to the same user
            var targetSession = await sessionRepository.GetByIdAsync(request.OnCompleteTargetSessionId);
            if (targetSession is null)
                return FleetError.NotFoundFor(nameof(Session), request.OnCompleteTargetSessionId);

            if (!string.Equals(targetSession.UserId, userContext.UserId, StringComparison.Ordinal))
                return FleetError.Unauthorized;

            var callback = new SessionCallback
            {
                Id = Guid.NewGuid().ToString(),
                SourceSessionId = session.Id,
                TargetSessionId = request.OnCompleteTargetSessionId,
                TargetInstanceId = request.OnCompleteTargetInstanceId,
                Status = "pending",
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            await sessionCallbackRepository.InsertAsync(callback);
        }

        return new CreateSessionResult(session, harnessInstance.InstanceId, workspace.Id);
    }

    // ── Resume ─────────────────────────────────────────────────────────────────

    public async Task<Result<Session>> ResumeSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions must be unarchived before they can be resumed.");

        var workspaceResult = await workspaceService.GetWorkspaceDirectoryAsync(session.WorkspaceId);
        if (workspaceResult.IsFailure)
            return workspaceResult.Error;

        var harness = harnessRegistry.GetByType(session.HarnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", session.HarnessType);

        var harnessRuntime = harnessRegistry.GetRuntimeByType(session.HarnessType);
        if (harnessRuntime is null)
            return FleetError.NotFoundFor("HarnessRuntime", session.HarnessType);

        // Load credentials using the session OWNER's userId.
        // The orchestrator never inspects credential contents — it passes them opaquely to the harness.
        var ownerCredentials = await credentialStore.GetDecryptedCredentialsAsync(session.UserId);
        var preparation = await harnessRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = session.UserId,
            UserCredentials = ownerCredentials,
            ModelId = null,
            WorkingDirectory = workspaceResult.Value
        }, ct);

        if (preparation is RuntimePreparation.NotReady notReadyResume)
        {
            var message = string.Join(" ", notReadyResume.Errors.Select(e => e.Message));
            return FleetError.ValidationError("Session.NotReady", message);
        }

        var resumeLaunchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts;

        IHarnessSession harnessInstance;
        try
        {
            if (session.HarnessResumeToken is not null && harness.Capabilities.SupportsResume)
            {
                harnessInstance = await harnessRuntime.ResumeAsync(new HarnessResumeOptions
                {
                    SessionId = session.Id,
                    WorkingDirectory = workspaceResult.Value,
                    OwnerUserId = session.UserId,
                    ResumeToken = session.HarnessResumeToken,
                    LaunchArtifacts = resumeLaunchArtifacts
                }, ct);
            }
            else
            {
                harnessInstance = await harnessRuntime.SpawnAsync(new HarnessSpawnOptions
                {
                    SessionId = session.Id,
                    WorkingDirectory = workspaceResult.Value,
                    OwnerUserId = session.UserId,
                    LaunchArtifacts = resumeLaunchArtifacts
                }, ct);
            }
        }
        catch (Exception ex)
        {
            LogSpawnFailed(ex, session.HarnessType);
            return FleetError.Unexpected;
        }

        await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,
            pid: harnessInstance.ProcessId,
            directory: workspaceResult.Value,
            url: string.Empty);

        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
        await sessionRepository.UpdateForResumeAsync(session.Id, harnessInstance.InstanceId);

        session.InstanceId = harnessInstance.InstanceId;
        return session;
    }

    // ── Fork ───────────────────────────────────────────────────────────────────

    public async Task<Result<CreateSessionResult>> ForkSessionAsync(
        string parentId,
        string? title = null,
        CancellationToken ct = default)
    {
        var parent = await sessionRepository.GetByIdAsync(parentId);
        if (parent is null)
            return FleetError.NotFoundFor(nameof(Session), parentId);

        // Fork reuses same workspace directory (no isolation)
        return await CreateSessionAsync(new CreateSessionRequest
        {
            Directory = parent.Directory,
            Title = title ?? $"Fork of {parent.Title}",
            ProjectId = parent.ProjectId,
            HarnessType = parent.HarnessType,
            IsolationStrategy = "existing",
            IsInternalRequest = true
        }, ct);
    }

    public async Task<Result<Session>> EnsureDelegatedChildSessionAsync(
        string parentSessionId,
        string childHarnessSessionId,
        string title,
        CancellationToken ct = default)
    {
        var parent = await sessionRepository.GetByIdAsync(parentSessionId);
        if (parent is null)
            return FleetError.NotFoundFor(nameof(Session), parentSessionId);

        var existing = await sessionRepository.GetByHarnessIdAsync(childHarnessSessionId);
        if (existing is not null)
            return existing;

        var harness = harnessRegistry.GetByType(parent.HarnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", parent.HarnessType);

        if (!harness.Capabilities.SupportsResume)
            return FleetError.ValidationError("Session.ResumeUnsupported", $"Harness '{parent.HarnessType}' does not support delegated child resume.");

        var delegationRuntime = harnessRegistry.GetRuntimeByType(parent.HarnessType);
        if (delegationRuntime is null)
            return FleetError.NotFoundFor("HarnessRuntime", parent.HarnessType);

        var childSessionId = Guid.NewGuid().ToString();
        IHarnessSession harnessInstance;
        try
        {
            harnessInstance = await delegationRuntime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = childSessionId,
                WorkingDirectory = parent.Directory,
                OwnerUserId = parent.UserId,
                ResumeToken = childHarnessSessionId,
                ProjectId = parent.ProjectId,
                ProjectName = await ResolveProjectNameAsync(parent.ProjectId)
            }, ct);
        }
        catch (Exception ex)
        {
            LogSpawnFailed(ex, parent.HarnessType);
            return FleetError.Unexpected;
        }

        var instanceResult = await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,
            pid: harnessInstance.ProcessId,
            directory: parent.Directory,
            url: string.Empty);
        if (instanceResult.IsFailure)
        {
            await SafeStopAsync(harnessInstance, ct);
            return instanceResult.Error;
        }

        var session = new Session
        {
            Id = childSessionId,
            WorkspaceId = parent.WorkspaceId,
            InstanceId = harnessInstance.InstanceId,
            ProjectId = parent.ProjectId,
            OpencodeSessionId = childHarnessSessionId,
            Title = string.IsNullOrWhiteSpace(title) ? "Delegated Session" : title,
            Status = "active",
            Directory = parent.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            ParentSessionId = parent.Id,
            LifecycleStatus = "running",
            HarnessType = parent.HarnessType,
            HarnessResumeToken = childHarnessSessionId,
            IsHidden = true,
            UserId = userContext.UserId
        };

        var createdAt = DateTime.UtcNow.ToString("O");
        session.CreatedAt = createdAt;
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.InsertAsync(session);
            await eventBroadcaster.BroadcastAsync("sessions", "session_created", new
            {
                sessionId = session.Id,
                instanceId = session.InstanceId,
                workspaceId = session.WorkspaceId,
                title = session.Title,
                projectId = session.ProjectId,
                parentSessionId = session.ParentSessionId,
                isHidden = true
            }, userContext.UserId, ct);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    SessionsToInsert = [session],
                    OutboxMessages =
                    [
                        CreateSessionLifecycleOutboxMessage(
                            "session_created",
                            new
                            {
                                sessionId = session.Id,
                                instanceId = session.InstanceId,
                                workspaceId = session.WorkspaceId,
                                title = session.Title,
                                projectId = session.ProjectId,
                                parentSessionId = session.ParentSessionId,
                                isHidden = true
                            },
                            createdAt,
                            userContext.UserId)
                    ]
                },
                ct);
        }

        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
        LogSessionCreated(session.Id, session.WorkspaceId, session.InstanceId);

        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: session.Id,
            ParentSessionId: parent.Id,
            ProjectId: session.ProjectId,
            ProjectName: await ResolveProjectNameAsync(session.ProjectId),
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
            UserId: userContext.UserId));

        return session;
    }

    // ── Prompt / Abort ─────────────────────────────────────────────────────────

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options = null,
        CancellationToken ct = default)
    {
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only. Unarchive before sending prompts.");

        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            await instanceResult.Value.SendPromptAsync(text, options, ct);
            return Unit.Value;
        }
        catch (InvalidOperationException ex)
        {
            LogPromptFailed(ex, id);
            return new FleetError("Session.PromptFailed", ex.Message);
        }
        catch (Exception ex)
        {
            LogPromptUnexpectedFailure(ex, id);
            return FleetError.Unexpected;
        }
    }

    public async Task<Result<ContextEnvelope>> PreviewAddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "Session.RetentionStatus",
                "Archived sessions are read-only. Unarchive before adding source context.");
        }

        var resolutionResult = await sessionSourceResolutionService.ResolveForSessionActionAsync(
            sessionId,
            source,
            SessionSourceActions.AddToSession,
            ct);
        if (resolutionResult.IsFailure)
            return resolutionResult.Error;

        var envelope = resolutionResult.Value.Input.ContextEnvelope;
        if (envelope is null)
        {
            return FleetError.ValidationError(
                "SessionSource.ContextEnvelope",
                "The selected session source did not resolve any previewable context.");
        }

        return envelope;
    }

    public async Task<Result<Unit>> AddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        bool confirm,
        CancellationToken ct = default)
    {
        if (!confirm)
        {
            return FleetError.ValidationError(
                "SessionSource.Confirm",
                "Source context must be explicitly confirmed before it can be added to a session.");
        }

        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "Session.RetentionStatus",
                "Archived sessions are read-only. Unarchive before adding source context.");
        }

        var resolutionResult = await sessionSourceResolutionService.ResolveForSessionActionAsync(
            sessionId,
            source,
            SessionSourceActions.AddToSession,
            ct);
        if (resolutionResult.IsFailure)
            return resolutionResult.Error;

        var envelope = resolutionResult.Value.Input.ContextEnvelope;
        if (envelope is null)
        {
            return FleetError.ValidationError(
                "SessionSource.ContextEnvelope",
                "The selected session source did not resolve any context.");
        }

        var prompt = $"[Source: {envelope.OriginLabel}]\n\n{envelope.Content}";
        var promptResult = await PromptSessionAsync(sessionId, prompt, null, ct);
        if (promptResult.IsFailure)
            return promptResult.Error;

        await sessionSourceUsageRepository.InsertAsync(new SessionSourceUsage
        {
            Id = Guid.NewGuid().ToString(),
            SessionId = sessionId,
            WorkspaceId = session.WorkspaceId,
            ProviderId = resolutionResult.Value.Input.Provenance.ProviderId,
            SourceType = resolutionResult.Value.Input.Provenance.SourceType,
            ActionId = resolutionResult.Value.Input.Provenance.ActionId,
            ResourceId = resolutionResult.Value.Input.Provenance.ResourceId,
            ResourceUrl = resolutionResult.Value.Input.Provenance.ResourceUrl,
            Title = resolutionResult.Value.Input.Provenance.Title,
            Summary = resolutionResult.Value.Input.Provenance.Summary,
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        return Unit.Value;
    }

    public async Task<Result<Unit>> AbortSessionAsync(string id, CancellationToken ct = default)
    {
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only. Unarchive before aborting.");

        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.AbortAsync(ct);
        return Unit.Value;
    }

    public async Task<Result<Unit>> CommandSessionAsync(
        string id,
        CommandOptions options,
        CancellationToken ct = default)
    {
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only. Unarchive before sending commands.");

        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.SendCommandAsync(options, ct);
        return Unit.Value;
    }

    // ── Messages / Diffs ───────────────────────────────────────────────────────

    public async Task<Result<MessagePage>> GetSessionMessagesAsync(
        string id,
        MessageQuery? query = null,
        CancellationToken ct = default)
    {
        // Validate session exists
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        return await GetPersistedMessagesAsync(id, query, ct);
    }

    public async Task<Result<IReadOnlyList<CommittedEvent>>> GetCommittedEventsAsync(
        string sessionId,
        long afterSequenceNumber,
        int? limit,
        CancellationToken ct = default)
    {
        _ = ct;

        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        var topic = $"session:{sessionId}";
        var rows = await outboxRepository.GetByTopicAfterAsync(
            topic,
            Math.Max(0, afterSequenceNumber),
            Math.Max(1, limit ?? options.Outbox.DispatchBatchSize));

        var events = rows
            .Select(row => new CommittedEvent(
                row.Id,
                row.Topic,
                row.Type,
                row.Payload,
                DateTimeOffset.Parse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)))
            .ToList();

        return Result.Success<IReadOnlyList<CommittedEvent>>(events);
    }

    private async Task<Result<MessagePage>> GetPersistedMessagesAsync(
        string sessionId,
        MessageQuery? query,
        CancellationToken ct)
    {
        _ = ct; // cancellation supported by caller; DB ops are fast
        var limit = query?.Limit ?? options.HistoryMessagePageSize;

        // Request limit + 1 to determine hasMore
        var rows = await messageRepository.GetBySessionAsync(sessionId, limit + 1, query?.Before);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Skip(rows.Count - limit).ToList() : (IReadOnlyList<PersistedMessage>)rows;

        var messages = MessagePersistenceService.ToHarnessMessages(pageRows);
        return Result.Success(new MessagePage(messages, hasMore));
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task<Result<Unit>> StopSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (session.Status is "stopped" or "completed" or "error" or "disconnected")
            return Unit.Value;

        var stoppedAt = DateTime.UtcNow.ToString("O");
        var liveInstance = instanceTracker.Get(session.InstanceId);
        if (liveInstance is not null)
        {
            await SafeStopAsync(liveInstance, ct);
            instanceTracker.Remove(session.InstanceId);
        }

        var instanceUpdateResult = await instanceService.UpdateInstanceStatusAsync(session.InstanceId, "stopped", stoppedAt);
        if (instanceUpdateResult.IsFailure)
            return instanceUpdateResult.Error;

        if (sessionActivityWriteService is null)
        {
            await sessionRepository.UpdateStatusAsync(id, "stopped", stoppedAt);
            await eventBroadcaster.BroadcastAsync("sessions", "session_stopped", new
            {
                sessionId = id,
                stoppedAt
            }, session.UserId, ct);
        }
        else
        {
            await sessionActivityWriteService.WriteAsync(
                new SessionActivityWriteRequest
                {
                    SessionStatusUpdates = [new SessionStatusUpdate { Id = id, Status = "stopped", StoppedAt = stoppedAt }],
                    OutboxMessages =
                    [
                        CreateSessionLifecycleOutboxMessage(
                            "session_stopped",
                            new { sessionId = id, stoppedAt },
                            stoppedAt,
                            session.UserId)
                    ]
                },
                ct);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> ArchiveSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return Unit.Value;

        var archivedAt = DateTime.UtcNow.ToString("O");
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.ArchiveAsync(id, archivedAt);
            await eventBroadcaster.BroadcastAsync("sessions", "session_archived", new
            {
                sessionId = id,
                archivedAt
            }, session.UserId, ct);
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
                            new { sessionId = id, archivedAt },
                            archivedAt,
                            session.UserId)
                    ]
                },
                ct);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> UnarchiveSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (!string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return Unit.Value;

        var changedAt = DateTime.UtcNow.ToString("O");
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.UnarchiveAsync(id);
            await eventBroadcaster.BroadcastAsync("sessions", "session_unarchived", new
            {
                sessionId = id
            }, session.UserId, ct);
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
                            new { sessionId = id },
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
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        var delegation = await delegationRepository.GetByChildSessionIdAsync(id);
        var parentDelegations = await delegationRepository.GetByParentSessionIdAsync(id);

        // Stop live instance if running
        var liveInstance = instanceTracker.Get(session.InstanceId);
        if (liveInstance is not null)
        {
            await SafeDeleteAsync(liveInstance, ct);
            instanceTracker.Remove(session.InstanceId);
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
                    new DelegationEventDto(
                        delegation.Id,
                        delegation.ParentSessionId,
                        delegation.ParentToolCallId,
                        delegation.ChildSessionId,
                        delegation.Title,
                        delegation.Status,
                        delegation.CreatedAt),
                    session.UserId,
                    ct);
            }

            if (parentDelegations.Count > 0)
                await delegationRepository.DeleteByParentSessionIdAsync(id);

            await sessionRepository.DeleteAsync(id);
            await eventBroadcaster.BroadcastAsync("sessions", "session_deleted", new
            {
                sessionId = id
            }, session.UserId, ct);
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
                    Payload = MessagePersistenceService.SerializePayload(new DelegationEventDto(
                        delegation.Id,
                        delegation.ParentSessionId,
                        delegation.ParentToolCallId,
                        delegation.ChildSessionId,
                        delegation.Title,
                        delegation.Status,
                        delegation.CreatedAt)),
                    UserId = session.UserId,
                    CreatedAt = deletedAtText,
                    AvailableAt = deletedAtText
                });
            }

            outboxMessages.Add(
                CreateSessionLifecycleOutboxMessage(
                    "session_deleted",
                    new { sessionId = id },
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
                    SessionDeletes = [id],
                    OutboxMessages = outboxMessages
                },
                ct);
        }

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

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<Result<IHarnessSession>> GetLiveInstanceAsync(string sessionId)
    {
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var session = sessionResult.Value;

        var instance = instanceTracker.Get(session.InstanceId);
        if (instance is null)
            return FleetError.NotFoundFor("Instance", session.InstanceId);

        return Result.Success<IHarnessSession>(instance);
    }

    private async Task<Result<Session>> GetSessionAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        return session;
    }

    private async Task<string?> ResolveScratchProjectIdAsync()
    {
        // Find the Scratch project by name convention
        var projects = await projectRepository.ListAsync();
        return projects.FirstOrDefault(p =>
            p.Name.Equals(ScratchProjectName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private async Task SafeStopAsync(IHarnessSession instance, CancellationToken ct)
    {
        try { await instance.StopAsync(ct); }
        catch (Exception ex) { LogStopFailed(ex, instance.InstanceId); }
    }

    private async Task SafeDeleteAsync(IHarnessSession instance, CancellationToken ct)
    {
        try { await instance.DeleteAsync(ct); }
        catch (Exception ex) { LogStopFailed(ex, instance.InstanceId); }
    }

    private static string GetDelegationTerminalStatus(string sessionStatus) => sessionStatus switch
    {
        "error" => "error",
        _ => "completed"
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to spawn harness {HarnessType}")]
    private partial void LogSpawnFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created: workspace={WorkspaceId} instance={InstanceId}")]
    private partial void LogSessionCreated(string sessionId, string workspaceId, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop instance {InstanceId}")]
    private partial void LogStopFailed(Exception ex, string instanceId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to retrieve messages for session {SessionId} — returning error result")]
    private partial void LogGetMessagesFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to send prompt to session {SessionId}")]
    private partial void LogPromptFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unexpected failure sending prompt to session {SessionId}")]
    private partial void LogPromptUnexpectedFailure(Exception ex, string sessionId);

    private async Task<string?> ResolveProjectNameAsync(string? projectId)
    {
        if (projectId is null)
            return null;

        var projects = await projectRepository.ListAsync();
        return projects.FirstOrDefault(p => p.Id == projectId)?.Name;
    }

    private static OutboxMessage CreateSessionLifecycleOutboxMessage(
        string eventType,
        object payload,
        string createdAt,
        string userId)
    {
        return new OutboxMessage
        {
            Topic = "sessions",
            Type = eventType,
            Payload = MessagePersistenceService.SerializePayload(payload),
            UserId = userId,
            CreatedAt = createdAt,
            AvailableAt = createdAt
        };
    }
}

// ── Request / Result DTOs ──────────────────────────────────────────────────────

/// <summary>Input for creating a new session.</summary>
public sealed record CreateSessionRequest
{
    public string? Directory { get; init; }
    public string? Title { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
    public string? HarnessType { get; init; }
    public string? ProjectId { get; init; }
    public string? InitialPrompt { get; init; }
    public SessionSourceSelection? Source { get; init; }
    /// <summary>If set, registers a completion callback to resume this target session.</summary>
    public string? OnCompleteTargetSessionId { get; init; }
    public string? OnCompleteTargetInstanceId { get; init; }
    /// <summary>
    /// When true, the request originates from an internal orchestrator operation (e.g. fork)
    /// and directory-path validation is bypassed. Must not be set from external API requests.
    /// </summary>
    internal bool IsInternalRequest { get; init; }
}

/// <summary>Result of a successful <see cref="SessionOrchestrator.CreateSessionAsync"/> call.</summary>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId);

public sealed record CommittedEvent(
    long SequenceNumber,
    string Topic,
    string Type,
    string Payload,
    DateTimeOffset Timestamp);
