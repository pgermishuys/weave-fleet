using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Terminals;
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
    GitDiffService? gitDiffService = null,
    ISessionTerminalCleanup? sessionTerminals = null,
    ISessionAppCleanup? sessionApps = null,
    IMessageRepository? messageRepository = null,
    SessionRecapService? sessionRecaps = null,
    IHarnessProfileRepository? harnessProfiles = null,
    SessionNotifier? sessionNotifier = null,
    ISessionScreenshotStore? sessionScreenshots = null,
    WeaveFleet.Application.Pages.IPageStore? sessionPages = null,
    WeaveFleet.Application.Memory.AgentMemoryService? agentMemory = null,
    HarnessAvailabilityCache? harnessAvailability = null,
    SessionFiles? sessionFiles = null,
    SessionActivation? sessionActivation = null,
    SessionPrompting? sessionPrompting = null) : ISessionActivator
{
    private readonly DelegationService _delegationService = delegationService;
    private readonly SessionFiles _files = sessionFiles
        ?? new SessionFiles(sessionRepository, eventBroadcaster, NullLogger<SessionFiles>.Instance);
    private readonly GitDiffService _gitDiffService = gitDiffService ?? new GitDiffService();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DelegatedChildLocks = new(StringComparer.Ordinal);

    // The services the facade delegates to. DI passes them in; a caller that constructs the orchestrator itself (tests)
    // gets them built from the orchestrator's own dependencies, once, so they share one activation.
    private SessionActivation? _activation;
    private SessionPrompting? _prompting;

    private SessionActivation Activation => _activation ??= sessionActivation
        ?? new SessionActivation(
            workspaceService,
            instanceService,
            harnessRegistry,
            instanceTracker,
            sessionRepository,
            projectRepository,
            eventBroadcaster,
            credentialStore,
            userPreferenceRepository,
            NullLogger<SessionActivation>.Instance,
            harnessProfiles,
            sessionNotifier);

    private SessionPrompting Prompting => _prompting ??= sessionPrompting
        ?? new SessionPrompting(
            sessionRepository,
            harnessRegistry,
            Activation,
            sessionActivityTracker,
            _delegationService,
            sessionSourceResolutionService,
            sessionSourceUsageRepository,
            eventBroadcaster,
            userContext,
            NullLogger<SessionPrompting>.Instance,
            messageRepository,
            sessionRecaps,
            agentMemory);

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

    private const string _openCodeHarnessType = "opencode";
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
    /// 5. Deliver the first message, if any
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

        // An agent starting a session: not past the depth Fleet allows (a runaway of agents starting agents).
        if (request.SpawnedBySessionId is { } spawnedBy
            && await SessionLineage.AgentSpawnDepthAsync(sessionRepository, spawnedBy) is var depth
            && depth >= SessionLineage.MaxAgentSpawnDepth)
        {
            return SessionLineage.TooDeep(depth);
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
        var harnessType = await ResolveHarnessTypeAsync(request, ct);
        var runtimeMode = await ResolveRuntimeModeAsync(harnessType).ConfigureAwait(false);
        var harness = harnessRegistry.GetByType(harnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);

        var harnessRuntime = harnessRegistry.GetRuntimeByType(harnessType);
        if (harnessRuntime is null)
            return FleetError.NotFoundFor("HarnessRuntime", harnessType);

        var profileResult = await ResolveNewSessionProfileAsync(harness, request.HarnessProfileId).ConfigureAwait(false);
        if (profileResult.IsFailure)
            return profileResult.Error;
        var profile = profileResult.Value;

        // Prepare runtime: load user credentials and call harness preparation pipeline.
        // The orchestrator passes the opaque credential bag to the harness — it does not
        // inspect, interpret, or filter the credentials itself.
        var userCredentials = await credentialStore.GetDecryptedCredentialsAsync(userContext.UserId);
        var preparation = await harnessRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = userContext.UserId,
            UserCredentials = userCredentials,
            ModelId = null, // model selection happens inside the session, not at creation time
            WorkingDirectory = workspaceIntent.Directory,
            Profile = profile
        }, ct);

        if (preparation is RuntimePreparation.NotReady notReady)
        {
            var message = string.Join(" ", notReady.Errors.Select(e => e.Message));
            return FleetError.ValidationError("Session.NotReady", message);
        }

        var launchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts;

        // Resolve or default project. A workflow run's next step goes where the run's steps are.
        var projectId = request.ProjectId
            ?? await ResolveWorkflowRunProjectIdAsync(request.WorkflowRunId)
            ?? await ResolveScratchProjectIdAsync();

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
            sourceResolutionResult.Value.Input.Provenance,
            workspaceIntent.BaseBranch,
            workspaceIntent.FetchOrigin,
            // The user's own words, not the assembled prompt: a GitHub issue's body would bury
            // the sentence a branch name is worth taking.
            request.BranchNamingText ?? request.InitialPrompt);
        if (workspaceResult.IsFailure)
            return workspaceResult.Error;

        var workspace = workspaceResult.Value;
        var canonicalWorkspaceDirectory = WorkspaceRootService.CanonicalizePath(workspace.Directory);
        var sessionId = Guid.NewGuid().ToString();
        var gitBaseline = await _gitDiffService.CaptureBaselineAsync(canonicalWorkspaceDirectory, sessionId, ct);

        // A harness that can take the first message after it starts gets it as an ordinary prompt
        // once the session exists (step 5), so it is saved and delivered like any other message.
        var sendInitialPromptAfterSpawn = initialPrompt is not null && !harness.Capabilities.RequiresInitialPrompt;

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
                InitialPrompt = sendInitialPromptAfterSpawn ? null : initialPrompt,
                Branch = workspace.Branch,
                ProjectId = projectId,
                ProjectName = projectName,
                ScenarioId = request.ScenarioId,
                LaunchArtifacts = launchArtifacts,
                WorkflowStep = request.WorkflowRunId is not null && !request.WorkflowUserFinishes,
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
            HarnessProfileId = profile?.Id,
            GitBaselineRef = gitBaseline?.RefName,
            GitRepoRoot = gitBaseline?.RepoRoot,
            UserId = userContext.UserId,
            HarnessResumeToken = harnessInstance.ResumeToken,
            SourceReference = request.SourceReference,
            Tags = request.Tags ?? [],
            // The first message and every prompt after it that names none go to these (PromptSessionCoreAsync).
            SelectedAgent = string.IsNullOrWhiteSpace(request.Agent) ? null : request.Agent.Trim(),
            SelectedProviderId = HasModel(request.ProviderId, request.ModelId) ? request.ProviderId!.Trim() : null,
            SelectedModelId = HasModel(request.ProviderId, request.ModelId) ? request.ModelId!.Trim() : null,
            WorkflowRunId = request.WorkflowRunId,
            WorkflowUserFinishes = request.WorkflowRunId is not null && request.WorkflowUserFinishes,
            SpawnedBySessionId = request.SpawnedBySessionId,
            SpawnKind = request.SpawnKind ?? (request.WorkflowRunId is not null ? SpawnKinds.Workflow : request.SpawnedBySessionId is not null ? SpawnKinds.Api : null),
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
                    ProjectId = session.ProjectId,
                    SpawnedBySessionId = session.SpawnedBySessionId,
                    SpawnKind = session.SpawnKind,
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
                                    ProjectId = session.ProjectId,
                                    SpawnedBySessionId = session.SpawnedBySessionId,
                                    SpawnKind = session.SpawnKind,
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

        // 4. Register callback (optional). Before the first message is sent: a turn that ends quickly must find it,
        // or only the poll would fire it.
        var callbackRegistered = false;
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
                // A harness given the first message with its spawn may be replying already. Otherwise the first reply
                // marks it started, as for any prompt.
                Status = initialPrompt is not null && !sendInitialPromptAfterSpawn
                    ? SessionCallbackStatuses.Started
                    : SessionCallbackStatuses.Pending,
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            await sessionCallbackRepository.InsertAsync(callback);
            callbackRegistered = true;
        }

        // 5. Deliver the first message.
        if (sendInitialPromptAfterSpawn)
        {
            // The page for this session subscribes only after create returns, too late for the
            // prompt's broadcast and possibly before the harness has stored the message, so the
            // message is also saved; the session snapshot shows it until the harness has it.
            var promptResult = await PromptSessionCoreAsync(
                sessionId,
                initialPrompt!,
                options: null,
                userMessageId: null,
                correlationId: null,
                saveUserMessage: true,
                rememberChoices: true,
                ct).ConfigureAwait(false);

            // The session exists either way; the user sees it without the message and can resend.
            if (promptResult.IsFailure)
                LogInitialPromptFailed(sessionId, promptResult.Error.Description);
            else if (callbackRegistered)
                // A turn that ends without a reply has still run.
                await sessionCallbackRepository.MarkSourceStartedAsync(sessionId);
        }
        else if (initialPrompt is not null)
        {
            // Broadcast initial prompt for optimistic UI update.
            // The harness runtime calls SendPromptAsync directly, bypassing PromptSessionAsync.
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

        return new CreateSessionResult(session, harnessInstance.InstanceId, workspace.Id, workspace.Branch);
    }

    public async Task<Result<Session>> EnsureDelegatedChildSessionAsync(
        string parentSessionId,
        string childHarnessSessionId,
        string title,
        CancellationToken ct = default)
    {
        // The harness announces a child from session.created and again from every task part
        // update, often at the same moment. Without the lock both callers miss the lookup and
        // each creates a child, so one harness session ends up as two Fleet sessions.
        // The lock is static because each caller resolves its own scoped orchestrator.
        var childLock = DelegatedChildLocks.GetOrAdd(childHarnessSessionId, static _ => new SemaphoreSlim(1, 1));
        await childLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await EnsureDelegatedChildSessionCoreAsync(parentSessionId, childHarnessSessionId, title, ct).ConfigureAwait(false);
        }
        finally
        {
            childLock.Release();
        }
    }

    private async Task<Result<Session>> EnsureDelegatedChildSessionCoreAsync(
        string parentSessionId,
        string childHarnessSessionId,
        string title,
        CancellationToken ct)
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

        // The child runs inside the parent's harness process, so it has to attach to the one the parent
        // woke on. It's prepared exactly as a parent wake is: anything the harness puts in the launch
        // (profile, credentials, the owner's built-in skills) picks the process, and a child prepared
        // differently binds to another one that doesn't know its session.
        var parentProfile = await ResolveSessionProfileAsync(parent.HarnessProfileId).ConfigureAwait(false);
        if (parentProfile.IsFailure)
            return parentProfile.Error;

        var parentCredentials = await credentialStore.GetDecryptedCredentialsAsync(parent.UserId).ConfigureAwait(false);
        var preparation = await delegationRuntime.PrepareRuntimeAsync(new RuntimePreparationContext
        {
            UserId = parent.UserId,
            UserCredentials = parentCredentials,
            ModelId = null,
            WorkingDirectory = canonicalParentDirectory,
            Profile = parentProfile.Value
        }, ct).ConfigureAwait(false);
        if (preparation is RuntimePreparation.NotReady notReady)
            return FleetError.ValidationError("Session.NotReady", string.Join(" ", notReady.Errors.Select(e => e.Message)));
        var childLaunchArtifacts = ((RuntimePreparation.Ready)preparation).Artifacts;

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
                ProjectName = await ResolveProjectNameAsync(parent.ProjectId),
                LaunchArtifacts = childLaunchArtifacts,
                ParentSessionId = parent.Id,
                DelegatedChild = true,
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
            HarnessProfileId = parent.HarnessProfileId,
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

        // A subagent asks as its parent does: Fleet prompts it only through its parent.
        await ApplyPermissionsAsync(session, harnessInstance, ct).ConfigureAwait(false);

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

    // ── Prompt / Abort (SessionPrompting) ──────────────────────────────────────

    public Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options = null,
        CancellationToken ct = default)
        => Prompting.PromptSessionAsync(id, text, options, ct);

    public Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        CancellationToken ct)
        => Prompting.PromptSessionAsync(id, text, options, userMessageId, ct);

    public Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
        => Prompting.PromptSessionAsync(id, text, options, userMessageId, correlationId, ct);

    public Task<Result<PromptSessionResult>> PromptSessionWithReceiptAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
        => Prompting.PromptSessionWithReceiptAsync(id, text, options, userMessageId, correlationId, ct);

    /// <inheritdoc cref="SessionPrompting.PromptSessionOnceAsync"/>
    public Task<Result<Unit>> PromptSessionOnceAsync(
        string id,
        string text,
        PromptOptions? options,
        CancellationToken ct = default)
        => Prompting.PromptSessionOnceAsync(id, text, options, ct);

    private Task<Result<PromptSessionResult>> PromptSessionCoreAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        bool saveUserMessage,
        bool rememberChoices,
        CancellationToken ct)
        => Prompting.PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, saveUserMessage, rememberChoices, ct);

    public Task<Result<ContextEnvelope>> PreviewAddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        CancellationToken ct = default)
        => Prompting.PreviewAddSourceToSessionAsync(sessionId, source, ct);

    public Task<Result<Unit>> AddSourceToSessionAsync(
        string sessionId,
        SessionSourceSelection source,
        bool confirm,
        CancellationToken ct = default)
        => Prompting.AddSourceToSessionAsync(sessionId, source, confirm, ct);

    public Task<Result<Unit>> AbortSessionAsync(string id, CancellationToken ct = default)
        => Prompting.AbortSessionAsync(id, ct);

    public Task<Result<Unit>> CommandSessionAsync(
        string id,
        CommandOptions options,
        CancellationToken ct = default)
        => Prompting.CommandSessionAsync(id, options, ct);

    private Task BroadcastUserMessageAsync(string sessionId, HarnessMessage message, CancellationToken ct)
        => Prompting.BroadcastUserMessageAsync(sessionId, message, ct);

    /// <inheritdoc cref="SessionPrompting.WithSessionChoices"/>
    internal static PromptOptions? WithSessionChoices(PromptOptions? options, Session session)
        => SessionPrompting.WithSessionChoices(options, session);

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

    private async Task<string> ResolveHarnessTypeAsync(CreateSessionRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.HarnessType))
        {
            return request.HarnessType;
        }

        var preferences = await userPreferenceRepository.GetAllAsync().ConfigureAwait(false);
        var harnesses = harnessAvailability is null
            ? null
            : (await harnessAvailability.GetAsync(fresh: false, ct).ConfigureAwait(false)).Harnesses;
        return HarnessPreferences.DefaultHarness(preferences, harnesses);
    }

    private async Task<string> ResolveRuntimeModeAsync(string harnessType)
    {
        if (!string.Equals(harnessType, _openCodeHarnessType, StringComparison.Ordinal))
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
        using var _ = BeginSessionScope(id);
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
        using var _ = BeginSessionScope(id);
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

    private static bool HasModel(string? providerId, string? modelId)
        => !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId);

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

    // ── Files (SessionFiles) ───────────────────────────────────────────────────

    public Task<Result<IReadOnlyList<string>>> FindSessionFilesAsync(string sessionId, string query, CancellationToken ct = default)
        => _files.FindSessionFilesAsync(sessionId, query, ct);

    public Task<Result<BrowseDirectoryResult>> BrowseSessionDirectoryAsync(string sessionId, string? path, CancellationToken ct = default)
        => _files.BrowseSessionDirectoryAsync(sessionId, path, ct);

    public Task<Result<ReadFileResult>> ReadSessionFileAsync(string sessionId, string? path, CancellationToken ct = default)
        => _files.ReadSessionFileAsync(sessionId, path, ct);

    public Task<Result<IReadOnlyList<ResolvedSessionFile>>> ResolveSessionFilesAsync(
        string sessionId,
        IReadOnlyList<string> paths,
        CancellationToken ct = default)
        => _files.ResolveSessionFilesAsync(sessionId, paths, ct);

    public Task<Result<SessionImage>> ResolveSessionImageAsync(string sessionId, string? path)
        => _files.ResolveSessionImageAsync(sessionId, path);

    public Task<Result<WriteFileResult>> WriteSessionFileAsync(
        string sessionId,
        string? path,
        string? content,
        string? baseHash,
        CancellationToken ct = default)
        => _files.WriteSessionFileAsync(sessionId, path, content, baseHash, ct);

    /// <inheritdoc cref="SessionFiles.HashFileBytes"/>
    public static string HashFileBytes(ReadOnlySpan<byte> bytes) => SessionFiles.HashFileBytes(bytes);

    // ── Activation (SessionActivation) ─────────────────────────────────────────

    /// <inheritdoc />
    public Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default)
        => Activation.ActivateSessionAsync(sessionId, ct);

    private Task<Result<IHarnessSession>> GetOrActivateInstanceAsync(Session session, CancellationToken ct)
        => Activation.GetOrActivateInstanceAsync(session, ct);

    private Task<Result<HarnessProfile?>> ResolveSessionProfileAsync(string? profileId)
        => Activation.ResolveSessionProfileAsync(profileId);

    private Task ApplyPermissionsAsync(Session session, IHarnessSession instance, CancellationToken ct)
        => Activation.ApplyPermissionsAsync(session, instance, ct);

    private Task EnsureEventSubscriptionReadyAsync(IHarnessSession instance, string sessionId, CancellationToken ct)
        => Activation.EnsureEventSubscriptionReadyAsync(instance, sessionId, ct);

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The profile a new session starts with: the one asked for, <see cref="HarnessProfileService.NoProfile"/> for
    /// none, or the harness's default when nothing was asked for.
    /// </summary>
    private async Task<Result<HarnessProfile?>> ResolveNewSessionProfileAsync(IHarness harness, string? requestedId)
    {
        if (requestedId == HarnessProfileService.NoProfile)
            return Result.Success<HarnessProfile?>(null);

        var supported = harnessProfiles is not null && HarnessProfileService.Supports(harness, options);
        if (requestedId is null)
            return supported ? Result.Success(await harnessProfiles!.GetDefaultAsync(harness.Type).ConfigureAwait(false)) : Result.Success<HarnessProfile?>(null);

        if (!supported)
            return FleetError.ValidationError("Session.Profile", $"{harness.DisplayName} sessions can't use profiles.");

        var profile = await harnessProfiles!.GetByIdAsync(requestedId).ConfigureAwait(false);
        if (profile is null || profile.HarnessType != harness.Type)
            return FleetError.ValidationError("Session.Profile", $"There's no {harness.DisplayName} profile with id '{requestedId}'.");
        return profile;
    }

    /// <summary>
    /// Answers the agent's ask <paramref name="requestId"/> in session <paramref name="id"/>, the session whose harness
    /// asked. Only a running harness has asks: one that isn't running has nothing waiting.
    /// </summary>
    public async Task<Result<Unit>> ReplyToPermissionAsync(
        string id,
        string requestId,
        string reply,
        string? message,
        CancellationToken ct = default)
    {
        using var _ = BeginSessionScope(id);
        if (!PermissionReplies.IsAnswer(reply))
            return FleetError.ValidationError("Permission.Reply", "Answer once, always or reject.");

        var sessionResult = await GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error;

        if (instanceTracker.Get(sessionResult.Value.InstanceId) is not { } instance)
            return FleetError.NotFoundFor("PermissionRequest", requestId);

        try
        {
            await instance.ReplyToPermissionAsync(requestId, reply, message, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return FleetError.NotFoundFor("PermissionRequest", requestId);
        }

        return Unit.Value;
    }

    private async Task<Result<Session>> GetSessionAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        return session;
    }

    private async Task<string?> ResolveWorkflowRunProjectIdAsync(string? workflowRunId)
        => workflowRunId is null
            ? null
            : (await sessionRepository.GetForWorkflowRunAsync(workflowRunId)).FirstOrDefault(s => s.ProjectId is not null)?.ProjectId;

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

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to retrieve messages for session {SessionId} — returning error result")]
    private partial void LogGetMessagesFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} was created but its first message could not be sent: {Reason}")]
    private partial void LogInitialPromptFailed(string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to fetch messages for session {SessionId} via proxy — returning empty result")]
    private partial void LogProxyMessageFetchFailed(Exception ex, string sessionId);

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
    /// <summary>
    /// The profile to start the session with. Null means the harness's default profile, if it has one;
    /// <see cref="HarnessProfileService.NoProfile"/> means none.
    /// </summary>
    public string? HarnessProfileId { get; init; }
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
    /// <summary>
    /// The agent the session starts with; null for the harness's default. Prompts that name no agent get it too.
    /// </summary>
    public string? Agent { get; init; }
    /// <summary>
    /// The model the session starts with, used only with <see cref="ModelId"/>; null for the agent's or the
    /// harness's default. Prompts that name no model get it too.
    /// </summary>
    public string? ProviderId { get; init; }
    /// <inheritdoc cref="ProviderId" />
    public string? ModelId { get; init; }
    /// <summary>
    /// The workflow run the session is a step of. Set only by the workflow runner: the session keeps the step tool,
    /// which every other session has hidden.
    /// </summary>
    internal string? WorkflowRunId { get; init; }
    /// <summary>
    /// The workflow step is one the user finishes: the session is made like any session that isn't a step, with the
    /// step tool hidden, so only the user can end it.
    /// </summary>
    internal bool WorkflowUserFinishes { get; init; }
    /// <summary>What a new worktree's branch is named from, when it isn't <see cref="InitialPrompt"/>.</summary>
    internal string? BranchNamingText { get; init; }
    /// <summary>
    /// The session whose agent asked for this one (<see cref="Session.SpawnedBySessionId"/>). Set only once Fleet knows
    /// who called, never from a request's body.
    /// </summary>
    public string? SpawnedBySessionId { get; init; }
    /// <summary>
    /// How the session came to be (<see cref="SpawnKinds"/>). Defaults to <see cref="SpawnKinds.Workflow"/> for a
    /// workflow step and <see cref="SpawnKinds.Api"/> when <see cref="SpawnedBySessionId"/> is set.
    /// </summary>
    public string? SpawnKind { get; init; }
}

/// <summary>Result of a successful <see cref="SessionOrchestrator.CreateSessionAsync"/> call.</summary>
/// <param name="Branch">
/// The branch the new workspace got, which for a worktree the naming templates chose here rather
/// than in the caller — so the caller can show the session's branch without guessing at it.
/// </param>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId, string? Branch = null);
