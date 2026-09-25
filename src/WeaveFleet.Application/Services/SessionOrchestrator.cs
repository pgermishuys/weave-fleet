using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    ISessionScreenshotStore? sessionScreenshots = null) : ISessionActivator
{
    private readonly DelegationService _delegationService = delegationService;
    private readonly GitDiffService _gitDiffService = gitDiffService ?? new GitDiffService();
    // Static because the orchestrator is scoped: opening a session wakes it from several requests at
    // once, and a per-request lock let each of them start its own harness for the same session.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ActivationLocks = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DelegatedChildLocks = new(StringComparer.Ordinal);

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
    /// 4. Deliver the first message, if any
    /// 5. Optionally register a completion callback
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

        // 6. Register callback (optional)
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

        return new CreateSessionResult(session, harnessInstance.InstanceId, workspace.Id, workspace.Branch);
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
            // A fork keeps the parent's profile, including none: without the id it would get the default.
            HarnessProfileId = parent.HarnessProfileId ?? HarnessProfileService.NoProfile,
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
        var result = await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, saveUserMessage: false, rememberChoices: true, ct).ConfigureAwait(false);
        return result.IsSuccess ? Unit.Value : result.Error;
    }

    public async Task<Result<PromptSessionResult>> PromptSessionWithReceiptAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        CancellationToken ct)
        => await PromptSessionCoreAsync(id, text, options, userMessageId, correlationId, saveUserMessage: false, rememberChoices: true, ct).ConfigureAwait(false);

    /// <summary>
    /// Prompts a session with an agent or model for this prompt only; later prompts that name none still get the
    /// session's own. Automations prompt existing sessions this way, so a run on a cheaper model doesn't change
    /// the model the session's owner picked.
    /// </summary>
    public async Task<Result<Unit>> PromptSessionOnceAsync(
        string id,
        string text,
        PromptOptions? options,
        CancellationToken ct = default)
    {
        var result = await PromptSessionCoreAsync(id, text, options, userMessageId: null, correlationId: null, saveUserMessage: false, rememberChoices: false, ct).ConfigureAwait(false);
        return result.IsSuccess ? Unit.Value : result.Error;
    }

    private async Task<Result<PromptSessionResult>> PromptSessionCoreAsync(
        string id,
        string text,
        PromptOptions? options,
        string? userMessageId,
        string? correlationId,
        bool saveUserMessage,
        bool rememberChoices,
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

        // What the caller named is remembered below; what it left out comes from the session.
        var requestedOptions = rememberChoices ? options : null;
        options = WithSessionChoices(options, sessionResult.Value);

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

            // Your reply is what a recap waits for: it clears the current one and counts toward the next.
            if (sessionRecaps is not null)
                await sessionRecaps.OnPromptSentAsync(id, sessionResult.Value.UserId, ct).ConfigureAwait(false);

            // Saved under the id the harness was given, so the snapshot can tell when the harness has its own copy.
            if (saveUserMessage && messageRepository is not null)
                await messageRepository.UpsertAsync(MessagePersistenceService.ToPersistedMessage(id, userMsg)).ConfigureAwait(false);

            // Remember an agent or model the prompt named, so later prompts that name none (after a refresh,
            // from an automation, or with "Default" picked) keep it rather than drop to the harness's default.
            // A model is only remembered as a pair; the API resolves the pair before it gets here.
            if (HasModel(requestedOptions?.ProviderId, requestedOptions?.ModelId)
                && (requestedOptions!.ProviderId != sessionResult.Value.SelectedProviderId
                    || requestedOptions.ModelId != sessionResult.Value.SelectedModelId))
            {
                await sessionRepository.UpdateSelectedModelAsync(id, requestedOptions.ProviderId!, requestedOptions.ModelId!);
            }

            if (requestedOptions?.Agent is { Length: > 0 } agent
                && !string.Equals(agent, sessionResult.Value.SelectedAgent, StringComparison.Ordinal))
            {
                await sessionRepository.UpdateSelectedAgentAsync(id, agent);
            }

            return new PromptSessionResult(EventId: null, effectiveCorrelationId, generatedMessageId);
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

        // Shown straight away as "/name arguments", under an id that sorts where it was sent. A harness that takes the
        // id stores the command's message under it; one that doesn't says which id it chose.
        options = options with { MessageId = AscendingMessageId.New() };
        var userMsg = MessagePersistenceService.CreateUserCommandMessage(options, DateTimeOffset.UtcNow);
        await BroadcastUserMessageAsync(id, userMsg, ct).ConfigureAwait(false);

        var harnessMessageId = await instanceResult.Value.SendCommandAsync(options, ct);

        // The harness's message holds what it made of the command (OpenCode's whole template). Remembering the command
        // under that message's id lets the conversation show "/name arguments" there too, after a reload.
        if (harnessMessageId is not null && messageRepository is not null && userMsg.Command is { } command)
            await messageRepository.SaveCommandAsync(id, harnessMessageId, command).ConfigureAwait(false);

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
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds()),
                message.Command),
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
                new CommittedMessageTime(message.Timestamp.ToUnixTimeMilliseconds()),
                message.Command),
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
        return Unit.Value;
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

        // Only once the session is gone: an archived one keeps its screenshots, since its conversation comes back.
        await DeleteScreenshotsAsync(id, ct);

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

    /// <summary>
    /// A prompt that names no agent or model gets the session's: the ones it started with or was last given. So
    /// "Default" in a session means what that session runs on, for people and automations alike.
    /// </summary>
    internal static PromptOptions? WithSessionChoices(PromptOptions? options, Session session)
    {
        var agent = string.IsNullOrWhiteSpace(options?.Agent) ? session.SelectedAgent : options.Agent;
        var namesModel = HasModel(options?.ProviderId, options?.ModelId);
        var useSessionModel = !namesModel && HasModel(session.SelectedProviderId, session.SelectedModelId);
        if (agent == options?.Agent && !useSessionModel)
            return options;

        var withChoices = (options ?? new PromptOptions()) with { Agent = agent };
        return useSessionModel
            ? withChoices with { ProviderId = session.SelectedProviderId, ModelId = session.SelectedModelId }
            : withChoices;
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

        // Only the filesystem is read, so the session's harness stays asleep.
        if (!Directory.Exists(sessionResult.Value.Directory))
            return Result.Success<IReadOnlyList<string>>(Array.Empty<string>());

        var matches = await WorkspaceFileSearch.FindAsync(sessionResult.Value.Directory, query, limit: 50, ct).ConfigureAwait(false);
        return Result.Success(matches);
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
            if (fileInfo.Length > MaxEditableFileBytes)
            {
                return new ReadFileResult(path, Content: null, IsBinary: false, IsTruncated: true);
            }

            var bytes = await File.ReadAllBytesAsync(targetFilePath, ct).ConfigureAwait(false);
            var hash = HashFileBytes(bytes);
            var content = DecodeText(bytes);
            return content is null
                ? new ReadFileResult(path, Content: null, IsBinary: true, IsTruncated: false, hash)
                : new ReadFileResult(path, content, IsBinary: false, IsTruncated: false, hash);
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

    /// <summary>The profile an existing session started with, as it is now.</summary>
    private async Task<Result<HarnessProfile?>> ResolveSessionProfileAsync(string? profileId)
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to end the terminals of session {SessionId}")]
    private partial void LogTerminalCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop the apps of session {SessionId}")]
    private partial void LogAppCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the screenshots of session {SessionId}")]
    private partial void LogScreenshotCleanupFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to retrieve messages for session {SessionId} — returning error result")]
    private partial void LogGetMessagesFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to send prompt to session {SessionId}")]
    private partial void LogPromptFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} was created but its first message could not be sent: {Reason}")]
    private partial void LogInitialPromptFailed(string sessionId, string reason);

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
}

/// <summary>Result of a successful <see cref="SessionOrchestrator.CreateSessionAsync"/> call.</summary>
/// <param name="Branch">
/// The branch the new workspace got, which for a worktree the naming templates chose here rather
/// than in the caller — so the caller can show the session's branch without guessing at it.
/// </param>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId, string? Branch = null);

/// <summary>Result of browsing a session directory.</summary>
public sealed record BrowseDirectoryResult(IReadOnlyList<BrowseEntry> Entries, string CurrentPath);

/// <summary>Represents a file or directory entry in a browsed directory.</summary>
public sealed record BrowseEntry(string Name, string RelativePath, bool IsDirectory);

/// <summary>Result of reading a session file.</summary>
/// <param name="Hash">SHA-256 of the file's bytes as lowercase hex; null when the file was too large to read.</param>
public sealed record ReadFileResult(string Path, string? Content, bool IsBinary, bool IsTruncated, string? Hash = null);
