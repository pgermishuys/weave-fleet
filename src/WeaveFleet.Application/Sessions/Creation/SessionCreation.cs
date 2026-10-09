using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Credentials;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions.Creation;

/// <summary>
/// Starting sessions: a new one (workspace, harness spawn, the session row, an optional completion callback, the first
/// message), a new one in another session's folder, and the hidden session a delegated subagent runs as, attached to its
/// parent's harness process.
/// </summary>
public sealed partial class SessionCreation(
    FleetOptions options,
    WorkspaceService workspaceService,
    InstanceService instanceService,
    SessionSourceResolutionService sessionSourceResolutionService,
    IHarnessRegistry harnessRegistry,
    InstanceTracker instanceTracker,
    ISessionRepository sessionRepository,
    ISessionSourceUsageRepository sessionSourceUsageRepository,
    ISessionCallbackRepository sessionCallbackRepository,
    IProjectRepository projectRepository,
    IEventBroadcaster eventBroadcaster,
    IAnalyticsCollector analyticsCollector,
    ICredentialStore credentialStore,
    IUserPreferenceRepository userPreferenceRepository,
    IUserContext userContext,
    SessionActivation activation,
    SessionPrompting prompting,
    ILogger<SessionCreation> logger,
    SessionActivityWriteService? sessionActivityWriteService = null,
    GitDiffService? gitDiffService = null,
    IHarnessProfileRepository? harnessProfiles = null,
    HarnessAvailabilityCache? harnessAvailability = null)
{
    private readonly GitDiffService _gitDiffService = gitDiffService ?? new GitDiffService();
    // Static because the session services are scoped: the harness announces a child from several places at once, each
    // in its own scope.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DelegatedChildLocks = new(StringComparer.Ordinal);

    private const string _openCodeHarnessType = "opencode";
    private const string _pooledOpenCodeHarnessPreferenceKey = "PooledOpenCodeHarness";
    private const string _runtimeModeAutomatic = "automatic";
    private const string _runtimeModeManual = "manual";
    private const string _scratchProjectName = "Scratch";

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
        using var _ = logger.BeginSessionScope(sessionId);
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
            var promptResult = await prompting.PromptSessionCoreAsync(
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
            await prompting.BroadcastUserMessageAsync(sessionId, userMsg, ct).ConfigureAwait(false);
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
        var parentProfile = await activation.ResolveSessionProfileAsync(parent.HarnessProfileId).ConfigureAwait(false);
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
        await activation.ApplyPermissionsAsync(session, harnessInstance, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Starts a new, empty session in session <paramref name="sessionId"/>'s folder, on the same harness and profile: what
    /// Fork did before it copied the conversation. Any harness can.
    /// </summary>
    public async Task<Result<CreateSessionResult>> StartSessionInFolderOfAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId).ConfigureAwait(false);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        return await CreateSessionAsync(new CreateSessionRequest
        {
            Directory = session.Directory,
            ProjectId = session.ProjectId,
            HarnessType = session.HarnessType,
            // The session's profile, including none: without the id it would get the default.
            HarnessProfileId = session.HarnessProfileId ?? HarnessProfileService.NoProfile,
            IsolationStrategy = "existing",
            // The folder is a session's, not one the caller named (see the cloud-mode check).
            IsInternalRequest = true,
        }, ct).ConfigureAwait(false);
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

    private static bool HasModel(string? providerId, string? modelId)
        => !string.IsNullOrWhiteSpace(providerId) && !string.IsNullOrWhiteSpace(modelId);

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

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to spawn harness {HarnessType}")]
    private partial void LogSpawnFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created: workspace={WorkspaceId} instance={InstanceId}")]
    private partial void LogSessionCreated(string sessionId, string workspaceId, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} was created but its first message could not be sent: {Reason}")]
    private partial void LogInitialPromptFailed(string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop instance {InstanceId}")]
    private partial void LogStopFailed(Exception ex, string instanceId);
}
