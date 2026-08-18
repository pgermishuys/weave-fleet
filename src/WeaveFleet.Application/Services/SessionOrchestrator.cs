using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
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
    ISessionMessageProxy sessionMessageProxy,
    DelegationService delegationService,
    ICredentialStore credentialStore,
    IUserPreferenceRepository userPreferenceRepository,
    IUserContext userContext,
    FleetOptions options,
    ISmartLinkRepository smartLinkRepository,
    SessionActivityTracker sessionActivityTracker,
    ILogger<SessionOrchestrator> logger,
    SessionActivityWriteService? sessionActivityWriteService = null,
    GitDiffService? gitDiffService = null) : ISessionActivator
{
    private readonly DelegationService _delegationService = delegationService;
    private readonly GitDiffService _gitDiffService = gitDiffService ?? new GitDiffService();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _activationLocks = new();

    private sealed class NoOpUserPreferenceRepository : IUserPreferenceRepository
    {
        public Task<string?> GetAsync(string key) => Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetAllAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetAsync(string key, string value) => Task.CompletedTask;
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
        ISessionMessageProxy sessionMessageProxy,
        DelegationService delegationService,
        ICredentialStore credentialStore,
        IUserPreferenceRepository userPreferenceRepository,
        IUserContext userContext,
        FleetOptions options,
        ISmartLinkRepository smartLinkRepository,
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
            sessionMessageProxy,
            delegationService,
            credentialStore,
            userPreferenceRepository,
            userContext,
            options,
            smartLinkRepository,
            new SessionActivityTracker(),
            logger,
            sessionActivityWriteService: null)
    {
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
        ISessionMessageProxy sessionMessageProxy,
        DelegationService delegationService,
        ICredentialStore credentialStore,
        IUserContext userContext,
        FleetOptions options,
        ISmartLinkRepository smartLinkRepository,
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
            sessionMessageProxy,
            delegationService,
            credentialStore,
            new NoOpUserPreferenceRepository(),
            userContext,
            options,
            smartLinkRepository,
            new SessionActivityTracker(),
            logger,
            sessionActivityWriteService: null)
    {
    }

    private const string _defaultHarnessTypePreferenceKey = "defaultHarnessType";
    private const string _fallbackDefaultHarnessType = "opencode";
    private const string _pooledOpenCodeHarnessPreferenceKey = "PooledOpenCodeHarness";
    private const string _runtimeModeAutomatic = "automatic";
    private const string _runtimeModeManual = "manual";
    private const string _scratchProjectName = "Scratch";
    private const string _lifecycleStatusRunning = "running";
    private const string _lifecycleStatusError = "error";
    private const string _activityStatusIdle = "idle";

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

        var initialPrompt = BuildCreateSessionInitialPrompt(
            request.InitialPrompt,
            sourceResolutionResult.Value.Input.ContextEnvelope,
            sourceResolutionResult.Value.Input.Provenance);

        // Resolve harness
        var harnessType = await ResolveHarnessTypeAsync(request);
        var runtimeMode = await ResolveRuntimeModeAsync(harnessType).ConfigureAwait(false);
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
        var canonicalWorkspaceDirectory = WorkspaceRootService.CanonicalizePath(workspace.Directory);
        var sessionId = Guid.NewGuid().ToString();
        var gitBaseline = await _gitDiffService.CaptureBaselineAsync(canonicalWorkspaceDirectory, sessionId, ct);

        // 2. Spawn harness instance
        using var _ = BeginSessionScope(sessionId);
        IHarnessSession harnessInstance;
        try
        {
            harnessInstance = await harnessRuntime.SpawnAsync(new HarnessSpawnOptions
            {
                SessionId = sessionId,
                WorkingDirectory = canonicalWorkspaceDirectory,
                OwnerUserId = userContext.UserId,
                InitialPrompt = initialPrompt,
                Branch = workspaceIntent.Branch,
                ProjectId = projectId,
                ProjectName = projectName,
                ScenarioId = request.ScenarioId,
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
            directory: canonicalWorkspaceDirectory,
            url: string.Empty);
        if (instanceResult.IsFailure)
        {
            // Best-effort delete: removes any eagerly-created OC session (pooled mode) before
            // giving up. DeleteAsync is preferred over StopAsync here because it also issues
            // the OC-level DELETE request, cleaning up the in-process session record.
            await SafeDeleteAsync(harnessInstance, ct);
            return instanceResult.Error;
        }

        // 4. Persist session
        // NOTE: the session row must be persisted BEFORE the instance is registered with the
        // tracker, because registration starts the HarnessEventRelay pump which resolves the
        // Fleet session id via the DB. Registering first races the pump against the insert.
        // For pooled/automatic sessions, HarnessResumeToken is set here from the eagerly-created
        // OpenCode session ID (available because SpawnAsync returns after OC session creation).
        // For non-pooled sessions, ResumeToken is null at spawn time and is updated later via
        // UpdateResumeTokenAsync when the harness creates the OC session on first prompt.
        var session = new Session
        {
            Id = sessionId,
            WorkspaceId = workspace.Id,
            InstanceId = harnessInstance.InstanceId,
            ProjectId = projectId,
            OpencodeSessionId = harnessInstance.InstanceId,
            Title = request.Title ?? "Untitled",
            Status = "active",
            Directory = canonicalWorkspaceDirectory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            HarnessType = harnessType,
            RuntimeMode = runtimeMode,
            GitBaselineRef = gitBaseline?.RefName,
            GitRepoRoot = gitBaseline?.RepoRoot,
            UserId = userContext.UserId,
            HarnessResumeToken = harnessInstance.ResumeToken,
            SourceReference = request.SourceReference,
            Tags = request.Tags ?? []
        };

        var createdAt = DateTime.UtcNow.ToString("O");
        session.CreatedAt = createdAt;
        if (sessionActivityWriteService is null)
        {
            try
            {
                await sessionRepository.InsertAsync(session);
            }
            catch
            {
                // Rollback: best-effort delete of the OC session (releases lease, removes binding
                // table entry, and issues OC-level DELETE). The resume token must not be logged or
                // persisted when this path is taken.
                await SafeDeleteAsync(harnessInstance, ct);
                throw;
            }

            // Track in-memory handle immediately after successful persistence (and before the
            // non-transactional broadcast) so the relay pump started by registration can resolve
            // the Fleet session id from the DB, and a broadcast failure cannot leave a persisted
            // session with an untracked instance.
            instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);

            await eventBroadcaster.BroadcastAsync("sessions", "session_created",
                JsonSerializer.SerializeToElement(new SessionCreatedOutboxPayload
                {
                    SessionId = session.Id,
                    InstanceId = harnessInstance.InstanceId,
                    WorkspaceId = workspace.Id,
                    Title = session.Title,
                    ProjectId = session.ProjectId
                }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
                userContext.UserId, ct);
        }
        else
        {
            try
            {
                await sessionActivityWriteService.WriteAsync(
                    new SessionActivityWriteRequest
                    {
                        SessionsToInsert = [session],
                        OutboxMessages =
                        [
                            CreateSessionLifecycleOutboxMessage(
                                "session_created",
                                JsonSerializer.Serialize(new SessionCreatedOutboxPayload
                                {
                                    SessionId = session.Id,
                                    InstanceId = harnessInstance.InstanceId,
                                    WorkspaceId = workspace.Id,
                                    Title = session.Title,
                                    ProjectId = session.ProjectId
                                }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
                                createdAt,
                                userContext.UserId)
                        ]
                    },
                    ct);
            }
            catch
            {
                // Rollback: best-effort delete of the OC session (releases lease, removes binding
                // table entry, and issues OC-level DELETE). The resume token must not be logged or
                // persisted when this path is taken.
                await SafeDeleteAsync(harnessInstance, ct);
                throw;
            }

            // Track in-memory handle immediately after successful persistence (see comment above).
            instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
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

        // Broadcast initial prompt for optimistic UI update.
        // The harness runtime calls SendPromptAsync directly, bypassing PromptSessionAsync.
        if (initialPrompt is not null)
        {
            var userMsg = MessagePersistenceService.CreateUserPromptMessage(initialPrompt, DateTimeOffset.UtcNow);
            await BroadcastUserMessageAsync(sessionId, userMsg, ct).ConfigureAwait(false);
        }

        // Emit analytics snapshot for the new session
        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: session.Id,
            ParentSessionId: null,
            ProjectId: projectId,
            ProjectName: projectName,
            WorkspaceDirectory: canonicalWorkspaceDirectory,
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
        using var _ = BeginSessionScope(id);
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions cannot be resumed.");

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

        // Update the DB mapping BEFORE registering: registration starts the relay pump, which
        // resolves the Fleet session id by instance id from the DB.
        await sessionRepository.UpdateForResumeAsync(session.Id, harnessInstance.InstanceId);
        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);

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
        var canonicalParentDirectory = WorkspaceRootService.CanonicalizePath(parent.Directory);
        IHarnessSession harnessInstance;
        try
        {
            harnessInstance = await delegationRuntime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = childSessionId,
                WorkingDirectory = canonicalParentDirectory,
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
            directory: canonicalParentDirectory,
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
            ActivityStatus = "idle",
            Directory = canonicalParentDirectory,
            CreatedAt = DateTime.UtcNow.ToString("O"),
            ParentSessionId = parent.Id,
            LifecycleStatus = "running",
            HarnessType = parent.HarnessType,
            RuntimeMode = parent.RuntimeMode,
            HarnessResumeToken = childHarnessSessionId,
            IsHidden = true,
            UserId = userContext.UserId,
        };

        var createdAt = DateTime.UtcNow.ToString("O");
        session.CreatedAt = createdAt;
        if (sessionActivityWriteService is null)
        {
            await sessionRepository.InsertAsync(session);
            await eventBroadcaster.BroadcastAsync("sessions", "session_created",
                JsonSerializer.SerializeToElement(new SessionCreatedOutboxPayload
                {
                    SessionId = session.Id,
                    InstanceId = session.InstanceId,
                    WorkspaceId = session.WorkspaceId,
                    Title = session.Title,
                    ProjectId = session.ProjectId,
                    ParentSessionId = session.ParentSessionId,
                    IsHidden = true
                }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
                userContext.UserId, ct);
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
                            JsonSerializer.Serialize(new SessionCreatedOutboxPayload
                            {
                                SessionId = session.Id,
                                InstanceId = session.InstanceId,
                                WorkspaceId = session.WorkspaceId,
                                Title = session.Title,
                                ProjectId = session.ProjectId,
                                ParentSessionId = session.ParentSessionId,
                                IsHidden = true
                            }, ApplicationJsonContext.Default.SessionCreatedOutboxPayload),
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
        => await PromptSessionAsync(id, text, options, userMessageId: null, correlationId: null, ct).ConfigureAwait(false);

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        CancellationToken ct)
        => await PromptSessionAsync(id, text, options, userMessageId, correlationId: null, ct).ConfigureAwait(false);

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
    {
        var result = await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, ct).ConfigureAwait(false);
        return result.IsSuccess ? Unit.Value : result.Error;
    }

    public async Task<Result<PromptSessionResult>> PromptSessionWithReceiptAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
        => await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, ct).ConfigureAwait(false);

    private async Task<Result<PromptSessionResult>> PromptSessionCoreAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
    {
        using var promptActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.prompt_session",
            ActivityKind.Internal);
        promptActivity?.SetTag(FleetInstrumentation.SessionIdTag, id);

        // Store trace context so the async relay pump can link response events back to this prompt.
        if (promptActivity is not null)
            sessionActivityTracker.SetPromptTraceContext(id, promptActivity.Context);

        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            // Generate ascending message ID for the user prompt.
            var generatedMessageId = AscendingMessageId.New();
            
            // Broadcast user message for optimistic UI update.
            // The harness echo is suppressed in HarnessEventPersistenceService to avoid duplicates.
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId)
                ? Guid.NewGuid().ToString()
                : correlationId;
            var userMsg = MessagePersistenceService.CreateUserPromptMessage(
                text,
                DateTimeOffset.UtcNow,
                options?.Agent,
                generatedMessageId,
                options?.Attachments);

            await BroadcastUserMessageAsync(id, userMsg, effectiveCorrelationId, ct).ConfigureAwait(false);

            // Ensure the event subscription is established before sending the prompt.
            // This prevents early events from being lost during activation/resume.
            await EnsureEventSubscriptionReadyAsync(instanceResult.Value, id, ct).ConfigureAwait(false);

            // Pass the generated message ID through to the harness.
            var promptOptionsWithMessageId = options is null
                ? new PromptOptions { MessageId = generatedMessageId }
                : options with { MessageId = generatedMessageId };

            await instanceResult.Value.SendPromptAsync(text, promptOptionsWithMessageId, ct);

            // Persist the model selection so a SPA refresh (which loses local state) can
            // fall back to it on the next prompt instead of silently using the harness
            // default. We only update when both ids are present — the API layer resolves
            // the provider/model pair via ResolveSessionModelAsync before reaching here.
            if (options?.ProviderId is { Length: > 0 } providerId
                && options.ModelId is { Length: > 0 } modelId)
            {
                await sessionRepository.UpdateSelectedModelAsync(id, providerId, modelId);
            }

            return new PromptSessionResult(EventId: null, effectiveCorrelationId);
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
        using var _ = BeginSessionScope(sessionId);
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        if (string.Equals(session.RetentionStatus, "archived", StringComparison.Ordinal))
        {
            return FleetError.ValidationError(
                "Session.RetentionStatus",
                "Archived sessions are read-only.");
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
        using var _ = BeginSessionScope(sessionId);
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
                "Archived sessions are read-only.");
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
        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.AbortAsync(ct);
        return Unit.Value;
    }

    public async Task<Result<Unit>> AnswerQuestionAsync(
        string id,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            await instanceResult.Value.AnswerQuestionAsync(requestId, answers, ct);
        }
        catch (NotSupportedException ex)
        {
            return new FleetError("Session.QuestionNotSupported", ex.Message);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> RejectQuestionAsync(
        string id,
        string requestId,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            await instanceResult.Value.RejectQuestionAsync(requestId, ct);
        }
        catch (NotSupportedException ex)
        {
            return new FleetError("Session.QuestionNotSupported", ex.Message);
        }

        return Unit.Value;
    }

    public async Task<Result<Unit>> CommandSessionAsync(
        string id,
        CommandOptions options,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (string.Equals(sessionResult.Value.RetentionStatus, "archived", StringComparison.Ordinal))
            return FleetError.ValidationError("Session.RetentionStatus", "Archived sessions are read-only.");

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        // Broadcast user command message for optimistic UI update.
        var userMsg = MessagePersistenceService.CreateUserCommandMessage(options, DateTimeOffset.UtcNow);
        await BroadcastUserMessageAsync(id, userMsg, ct).ConfigureAwait(false);

        await instanceResult.Value.SendCommandAsync(options, ct);
        return Unit.Value;
    }

    // ── Messages / Diffs ───────────────────────────────────────────────────────

    public async Task<Result<MessagePage>> GetSessionMessagesAsync(
        string id,
        MessageQuery? query = null,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        // Validate session exists
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        return await GetPersistedMessagesAsync(id, query, ct);
    }

    private async Task<Result<MessagePage>> GetPersistedMessagesAsync(
        string sessionId,
        MessageQuery? query,
        CancellationToken ct)
    {
        var limit = query?.Limit ?? options.HistoryMessagePageSize;
        var before = query?.Before;

        try
        {
            // Delegate to the proxy, which will fetch from opencode if available,
            // or fall back to persisted messages if the harness is unavailable.
            return await sessionMessageProxy.GetMessagesAsync(sessionId, limit, before, ct);
        }
        catch (Exception ex)
        {
            LogProxyMessageFetchFailed(ex, sessionId);
            // Return empty result on failure (503-equivalent behavior)
            return Result.Success(new MessagePage([], false));
        }
    }

    private async Task BroadcastUserMessageAsync(
        string sessionId,
        HarnessMessage message,
        CancellationToken ct)
    {
        if (message.Role is not "user")
            return;

        var parts = new List<JsonElement>(message.Parts.Count);
        for (var index = 0; index < message.Parts.Count; index++)
        {
            var partPayload = MessagePersistenceService.BuildCommittedMessagePartPayload(
                message.Id,
                sessionId,
                message.Parts[index],
                index);
            if (partPayload.HasValue)
                parts.Add(partPayload.Value);
        }

        var payload = JsonSerializer.SerializeToElement(new CommittedMessage(
            new CommittedMessageInfo(
                message.Id,
                message.Role,
                sessionId,
                message.Agent,
                message.ModelId,
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds())),
            parts),
            ApplicationJsonContext.Default.CommittedMessage);

        await eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            EventTypes.MessageUpdated,
            payload,
            userContext.UserId,
            ct).ConfigureAwait(false);
    }

    private async Task BroadcastUserMessageAsync(
        string sessionId,
        HarnessMessage message,
        string correlationId,
        CancellationToken ct)
    {
        if (message.Role is not "user")
            return;

        var parts = new List<JsonElement>(message.Parts.Count);
        for (var index = 0; index < message.Parts.Count; index++)
        {
            var partPayload = MessagePersistenceService.BuildCommittedMessagePartPayload(
                message.Id,
                sessionId,
                message.Parts[index],
                index);
            if (partPayload.HasValue)
                parts.Add(partPayload.Value);
        }

        var payload = JsonSerializer.SerializeToElement(new CommittedUserPromptMessage(
            new CommittedMessageInfo(
                message.Id,
                message.Role,
                sessionId,
                message.Agent,
                message.ModelId,
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds())),
            parts,
            correlationId),
            ApplicationJsonContext.Default.CommittedUserPromptMessage);

        await eventBroadcaster.BroadcastAsync(
            $"session:{sessionId}",
            EventTypes.MessageUpdated,
            payload,
            userContext.UserId,
            ct).ConfigureAwait(false);
    }

    private static string? BuildCreateSessionInitialPrompt(
        string? initialPrompt,
        ContextEnvelope? contextEnvelope,
        ProvenanceRecord provenance)
    {
        var normalizedInitialPrompt = string.IsNullOrWhiteSpace(initialPrompt)
            ? null
            : initialPrompt.Trim();

        if (contextEnvelope is null)
        {
            return normalizedInitialPrompt;
        }

        var sourcePrompt = BuildSourcePrompt(contextEnvelope, provenance);
        if (normalizedInitialPrompt is null)
        {
            return sourcePrompt;
        }

        return $"{sourcePrompt}\n\n{normalizedInitialPrompt}";
    }

    private static string BuildSourcePrompt(ContextEnvelope contextEnvelope, ProvenanceRecord provenance)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("[Source: ").Append(contextEnvelope.OriginLabel).Append(']');

        if (!string.IsNullOrWhiteSpace(provenance.ResourceId))
        {
            builder.Append("\n[Resource: ").Append(provenance.ResourceId).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(provenance.ResourceUrl))
        {
            builder.Append("\n[URL: ").Append(provenance.ResourceUrl).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(provenance.SourceType))
        {
            builder.Append("\n[Type: ").Append(provenance.SourceType).Append(']');
        }

        builder.Append("\n\n").Append(contextEnvelope.Content);
        return builder.ToString();
    }

    private async Task<string> ResolveHarnessTypeAsync(CreateSessionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.HarnessType))
        {
            return request.HarnessType;
        }

        var preferredHarnessType = await userPreferenceRepository.GetAsync(_defaultHarnessTypePreferenceKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(preferredHarnessType)
            ? _fallbackDefaultHarnessType
            : preferredHarnessType;
    }

    private async Task<string> ResolveRuntimeModeAsync(string harnessType)
    {
        if (!string.Equals(harnessType, _fallbackDefaultHarnessType, StringComparison.Ordinal))
        {
            return _runtimeModeManual;
        }

        var pooledPreference = await userPreferenceRepository.GetAsync(_pooledOpenCodeHarnessPreferenceKey).ConfigureAwait(false);
        var pooledModeEnabled = string.IsNullOrWhiteSpace(pooledPreference)
            ? options.Harness.PooledOpenCodeHarness
            : string.Equals(pooledPreference, "true", StringComparison.OrdinalIgnoreCase);

        return pooledModeEnabled ? _runtimeModeAutomatic : _runtimeModeManual;
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task<Result<Unit>> StopSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
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
            await eventBroadcaster.BroadcastAsync("sessions", "session_stopped",
                JsonSerializer.SerializeToElement(new SessionStoppedOutboxPayload(id, stoppedAt), ApplicationJsonContext.Default.SessionStoppedOutboxPayload),
                session.UserId, ct);
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
                            JsonSerializer.Serialize(new SessionStoppedOutboxPayload(id, stoppedAt), ApplicationJsonContext.Default.SessionStoppedOutboxPayload),
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
        using var _ = BeginSessionScope(id);
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

        return Unit.Value;
    }

#pragma warning disable CA1822 // Interface method cannot be static
    public Task<Result<Unit>> UnarchiveSessionAsync(string id, CancellationToken ct = default)
    {
        return Task.FromResult<Result<Unit>>(
            FleetError.ValidationError("Session.RetentionStatus", "Archived sessions cannot be unarchived."));
    }
#pragma warning restore CA1822

    public async Task<Result<Unit>> DeleteSessionAsync(string id, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
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

    // ── Session-scoped capabilities ────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<ProviderInfo>>> GetSessionModelsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var providers = await instanceResult.Value.GetProvidersAsync(ct);
        return Result.Success(providers);
    }

    public async Task<Result<IReadOnlyList<CommandInfo>>> GetSessionCommandsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var commands = await instanceResult.Value.GetCommandsAsync(ct);
        return Result.Success(commands);
    }

    public async Task<Result<IReadOnlyList<AgentInfo>>> GetSessionAgentsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        var agents = await instanceResult.Value.GetAgentsAsync(ct);
        return Result.Success(agents);
    }

    public async Task<Result<IReadOnlyList<string>>> FindSessionFilesAsync(
        string sessionId,
        string query,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var instanceResult = await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(sessionResult.Value.Directory))
            return Result.Success<IReadOnlyList<string>>(Array.Empty<string>());

        // Normalize query separators to the OS path separator for consistent matching
        var normalizedQuery = query.Replace('/', Path.DirectorySeparatorChar)
                                   .Replace('\\', Path.DirectorySeparatorChar);

        var files = Directory
            .EnumerateFiles(sessionResult.Value.Directory, "*", SearchOption.AllDirectories)
            .Select(f => f[sessionResult.Value.Directory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(relative => relative.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .ToArray();

        return Result.Success<IReadOnlyList<string>>(files);
    }

    public async Task<Result<BrowseDirectoryResult>> BrowseSessionDirectoryAsync(
        string sessionId,
        string? path,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var sessionDirectory = sessionResult.Value.Directory;
        if (!Directory.Exists(sessionDirectory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        // Normalize path separators to support both forward and backslashes on all platforms
        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? null
            : path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        // Resolve target directory with path traversal protection
        var targetDirectory = string.IsNullOrWhiteSpace(normalizedPath)
            ? sessionDirectory
            : Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));

        var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);
        if (!IsSameOrChildPath(targetDirectory, sessionDirectoryFullPath))
            return FleetError.ValidationError("Session.Directory", "Path traversal is not allowed.");

        if (!Directory.Exists(targetDirectory))
            return FleetError.ValidationError("Session.Directory", "Directory does not exist.");

        // Enumerate entries
        var entries = Directory.EnumerateFileSystemEntries(targetDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(fullPath =>
            {
                var name = Path.GetFileName(fullPath);
                var isDirectory = Directory.Exists(fullPath);
                var relativePath = Path.GetRelativePath(sessionDirectoryFullPath, fullPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                return new BrowseEntry(name, relativePath, isDirectory);
            })
            .ToList();

        // Filter out .git directory
        var filteredEntries = entries.Where(e => !string.Equals(e.Name, ".git", StringComparison.Ordinal)).ToList();

        // Sort: directories first, then files, both alphabetical
        var sortedEntries = filteredEntries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var currentPath = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').Trim('/');

        return new BrowseDirectoryResult(sortedEntries, currentPath);
    }

    public async Task<Result<ReadFileResult>> ReadSessionFileAsync(
        string sessionId,
        string? path,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        if (string.IsNullOrWhiteSpace(path))
            return FleetError.ValidationError("Session.File", "Path parameter is required.");

        var sessionResult = await GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        var sessionDirectory = sessionResult.Value.Directory;
        if (!Directory.Exists(sessionDirectory))
            return FleetError.ValidationError("Session.Directory", "Session directory does not exist.");

        // Normalize path separators to support both forward and backslashes on all platforms
        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

        // Resolve target file with path traversal protection
        // Path.GetFullPath will normalize separators (both / and \ work on all platforms)
        var targetFilePath = Path.GetFullPath(Path.Combine(sessionDirectory, normalizedPath));
        var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);
        if (!IsSameOrChildPath(targetFilePath, sessionDirectoryFullPath))
            return FleetError.ValidationError("Session.File", "Path traversal is not allowed.");

        if (!File.Exists(targetFilePath))
            return FleetError.NotFoundFor("File", path);

        try
        {
            var fileInfo = new FileInfo(targetFilePath);
            const int maxFileContentBytes = 512 * 1024;
            if (fileInfo.Length > maxFileContentBytes)
            {
                return new ReadFileResult(path, Content: null, IsBinary: false, IsTruncated: true);
            }

            var bytes = await File.ReadAllBytesAsync(targetFilePath, ct).ConfigureAwait(false);
            if (bytes.Contains((byte)0))
            {
                return new ReadFileResult(path, Content: null, IsBinary: true, IsTruncated: false);
            }

            try
            {
                var content = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
                return new ReadFileResult(path, content, IsBinary: false, IsTruncated: false);
            }
            catch (System.Text.DecoderFallbackException)
            {
                return new ReadFileResult(path, Content: null, IsBinary: true, IsTruncated: false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return FleetError.NotFoundFor("File", path);
        }
    }

    // ── ISessionActivator ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(sessionId);
        var sessionResult = await GetSessionAsync(sessionId).ConfigureAwait(false);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        return await GetOrActivateInstanceAsync(sessionResult.Value, ct).ConfigureAwait(false);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<Result<IHarnessSession>> GetOrActivateInstanceAsync(Session session, CancellationToken ct)
    {
        using var activateActivity = FleetInstrumentation.ActivitySource.StartActivity(
            "fleet.activate_instance",
            ActivityKind.Internal);
        activateActivity?.SetTag(FleetInstrumentation.SessionIdTag, session.Id);

        var instance = instanceTracker.Get(session.InstanceId);
        if (instance is not null)
            return Result.Success<IHarnessSession>(instance);

        if (string.IsNullOrWhiteSpace(session.HarnessResumeToken))
            return FleetError.NotFoundFor("Instance", session.InstanceId);

        // Do not auto-activate manual sessions that were explicitly stopped or completed.
        // Automatic sessions (pooled mode) should auto-activate even when stopped.
        if (session.RuntimeMode is "manual" && session.Status is "stopped" or "completed" or "error")
            return FleetError.NotFoundFor("Instance", session.InstanceId);

        var activationLock = _activationLocks.GetOrAdd(session.Id, static _ => new SemaphoreSlim(1, 1));
        await activationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentSession = await sessionRepository.GetByIdAsync(session.Id).ConfigureAwait(false);
            if (currentSession is null)
                return FleetError.NotFoundFor(nameof(Session), session.Id);

            if (string.IsNullOrWhiteSpace(currentSession.HarnessResumeToken))
                return FleetError.NotFoundFor("Instance", currentSession.InstanceId);

            // Re-check status under lock — session may have been stopped concurrently.
            if (currentSession.RuntimeMode is "manual" && currentSession.Status is "stopped" or "completed" or "error")
                return FleetError.NotFoundFor("Instance", currentSession.InstanceId);

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

    private async Task<Result<IHarnessSession>> ActivateSessionAsync(Session session, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(session.HarnessResumeToken))
            return FleetError.NotFoundFor("Instance", session.InstanceId);

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

        var ownerCredentials = await credentialStore.GetDecryptedCredentialsAsync(session.UserId).ConfigureAwait(false);
        var preparation = await harnessRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = session.UserId,
            UserCredentials = ownerCredentials,
            ModelId = null,
            WorkingDirectory = workspaceResult.Value
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
            harnessInstance = await harnessRuntime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = session.Id,
                WorkingDirectory = workspaceResult.Value,
                OwnerUserId = session.UserId,
                ResumeToken = session.HarnessResumeToken,
                ProjectId = session.ProjectId,
                ProjectName = projectName,
                LaunchArtifacts = launchArtifacts
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
        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
        session.InstanceId = harnessInstance.InstanceId;
        session.Status = "active";
        session.LifecycleStatus = _lifecycleStatusRunning;
        session.ActivityStatus = _activityStatusIdle;
        session.StoppedAt = null;
        await BroadcastAutomaticActivationStatusAsync(session, _activityStatusIdle, _lifecycleStatusRunning, ct).ConfigureAwait(false);

        return Result.Success<IHarnessSession>(harnessInstance);
    }

    private async Task MarkAutomaticActivationErrorAsync(Session session, CancellationToken ct)
    {
        await sessionRepository.UpdateStatusAsync(session.Id, _lifecycleStatusError).ConfigureAwait(false);
        session.Status = _lifecycleStatusError;
        session.LifecycleStatus = _lifecycleStatusError;
        session.ActivityStatus = _activityStatusIdle;
        await BroadcastAutomaticActivationStatusAsync(session, _lifecycleStatusError, _lifecycleStatusError, ct).ConfigureAwait(false);
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
        => SessionCapabilitiesResolver.Resolve(
            session.RuntimeMode,
            lifecycleStatus,
            session.RetentionStatus,
            activityStatus,
            instanceTracker.Get(session.InstanceId) is not null);

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
            p.Name.Equals(_scratchProjectName, StringComparison.OrdinalIgnoreCase))?.Id;
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
        Message = "Failed to automatically activate session {SessionId} for harness {HarnessType}")]
    private partial void LogAutomaticActivationFailed(Exception ex, string sessionId, string harnessType);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Unexpected failure sending prompt to session {SessionId}")]
    private partial void LogPromptUnexpectedFailure(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Event subscription readiness timed out for session {SessionId} after {TimeoutMs}ms — proceeding with prompt")]
    private partial void LogSubscriptionReadinessTimeout(string sessionId, int timeoutMs);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to fetch messages for session {SessionId} via proxy — returning empty result")]
    private partial void LogProxyMessageFetchFailed(Exception ex, string sessionId);

    /// <summary>
    /// Waits for the harness event subscription to be established before proceeding.
    /// This ensures events emitted immediately after activation/resume are not lost.
    /// Times out after 5 seconds and proceeds with a warning rather than failing the operation.
    /// </summary>
    private async Task EnsureEventSubscriptionReadyAsync(
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

    private IDisposable? BeginSessionScope(string sessionId)
    {
        Activity.Current?.SetTag(FleetInstrumentation.SessionIdTag, sessionId);
        return logger.BeginScope(new Dictionary<string, object> { [FleetInstrumentation.SessionIdTag] = sessionId });
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var root = TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (PathsEqual(candidate, root))
            return true;

        return candidate.StartsWith(EnsureEndingDirectorySeparator(root), PathStringComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimEndingDirectorySeparator(left),
            TrimEndingDirectorySeparator(right),
            PathStringComparison);

    private static string EnsureEndingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string TrimEndingDirectorySeparator(string path) =>
        Path.GetPathRoot(path) == path
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathStringComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
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
    /// Optional beta-tester scenario id. Only honoured when fleet runs with --harness=test;
    /// production harnesses ignore it. The orchestrator passes it through to
    /// <see cref="HarnessSpawnOptions.ScenarioId"/> at spawn time.
    /// </summary>
    public string? ScenarioId { get; init; }
    /// <summary>
    /// When true, the request originates from an internal orchestrator operation (e.g. fork)
    /// and directory-path validation is bypassed. Must not be set from external API requests.
    /// </summary>
    internal bool IsInternalRequest { get; init; }
    /// <summary>
    /// Optional automation reference. When set, links this session to an automation execution.
    /// </summary>
    public string? SourceReference { get; init; }
    /// <summary>
    /// Optional tags for categorizing and filtering sessions.
    /// </summary>
    public List<string>? Tags { get; init; }
}

/// <summary>Result of a successful <see cref="SessionOrchestrator.CreateSessionAsync"/> call.</summary>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId);

/// <summary>Result of browsing a session directory.</summary>
public sealed record BrowseDirectoryResult(IReadOnlyList<BrowseEntry> Entries, string CurrentPath);

/// <summary>Represents a file or directory entry in a browsed directory.</summary>
public sealed record BrowseEntry(string Name, string RelativePath, bool IsDirectory);

/// <summary>Result of reading a session file.</summary>
public sealed record ReadFileResult(string Path, string? Content, bool IsBinary, bool IsTruncated);
