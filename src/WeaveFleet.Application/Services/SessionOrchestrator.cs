using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Credentials;
using WeaveFleet.Application.Diagnostics;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Application.Sessions.Asks;
using WeaveFleet.Application.Sessions.Catalog;
using WeaveFleet.Application.Sessions.Compaction;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Application.Sessions.History;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.Sessions.Shell;
using WeaveFleet.Application.Sessions.Work;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
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
    SessionPrompting? sessionPrompting = null,
    SessionAsks? sessionAsks = null,
    SessionCatalog? sessionCatalog = null,
    SessionHistory? sessionHistory = null,
    SessionCompaction? sessionCompaction = null,
    SessionShellCommands? sessionShellCommands = null,
    SessionWork? sessionWork = null,
    SessionCreation? sessionCreation = null) : ISessionActivator
{
    private readonly DelegationService _delegationService = delegationService;
    private readonly SessionFiles _files = sessionFiles
        ?? new SessionFiles(sessionRepository, eventBroadcaster, NullLogger<SessionFiles>.Instance);
    private readonly GitDiffService _gitDiffService = gitDiffService ?? new GitDiffService();

    // The services the facade delegates to. DI passes them in; a caller that constructs the orchestrator itself (tests)
    // gets them built from the orchestrator's own dependencies, once, so they share one activation.
    private SessionActivation? _activation;
    private SessionPrompting? _prompting;
    private SessionAsks? _asks;
    private SessionCatalog? _catalog;
    private SessionHistory? _history;
    private SessionCompaction? _compaction;
    private SessionShellCommands? _shellCommands;
    private SessionWork? _work;
    private SessionCreation? _creation;

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

    private SessionAsks Asks => _asks ??= sessionAsks
        ?? new SessionAsks(sessionRepository, instanceTracker, Activation, NullLogger<SessionAsks>.Instance);

    private SessionCatalog Catalog => _catalog ??= sessionCatalog
        ?? new SessionCatalog(sessionRepository, Activation, NullLogger<SessionCatalog>.Instance);

    private SessionHistory History => _history ??= sessionHistory
        ?? new SessionHistory(sessionRepository, sessionMessageProxy, options, NullLogger<SessionHistory>.Instance);

    private SessionCompaction Compaction => _compaction ??= sessionCompaction
        ?? new SessionCompaction(sessionRepository, harnessRegistry, sessionActivityTracker, Activation, NullLogger<SessionCompaction>.Instance);

    private SessionShellCommands ShellCommands => _shellCommands ??= sessionShellCommands
        ?? new SessionShellCommands(sessionRepository, harnessRegistry, Activation, NullLogger<SessionShellCommands>.Instance);

    private SessionWork Work => _work ??= sessionWork
        ?? new SessionWork(sessionRepository, instanceTracker, _delegationService);

    private SessionCreation Creation => _creation ??= sessionCreation
        ?? new SessionCreation(
            options,
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            harnessRegistry,
            instanceTracker,
            sessionRepository,
            sessionSourceUsageRepository,
            sessionCallbackRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            credentialStore,
            userPreferenceRepository,
            userContext,
            Activation,
            Prompting,
            NullLogger<SessionCreation>.Instance,
            sessionActivityWriteService,
            _gitDiffService,
            harnessProfiles,
            harnessAvailability);

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

    private const string _lifecycleStatusRunning = "running";
    private const string _activityStatusIdle = "idle";

    // ── Create (SessionCreation) ───────────────────────────────────────────────

    /// <inheritdoc cref="SessionCreation.CreateSessionAsync"/>
    public Task<Result<CreateSessionResult>> CreateSessionAsync(CreateSessionRequest request, CancellationToken ct = default)
        => Creation.CreateSessionAsync(request, ct);

    /// <inheritdoc cref="SessionCreation.EnsureDelegatedChildSessionAsync"/>
    public Task<Result<Session>> EnsureDelegatedChildSessionAsync(
        string parentSessionId,
        string childHarnessSessionId,
        string title,
        CancellationToken ct = default)
        => Creation.EnsureDelegatedChildSessionAsync(parentSessionId, childHarnessSessionId, title, ct);

    /// <inheritdoc cref="SessionCreation.StartSessionInFolderOfAsync"/>
    public Task<Result<CreateSessionResult>> StartSessionInFolderOfAsync(string sessionId, CancellationToken ct = default)
        => Creation.StartSessionInFolderOfAsync(sessionId, ct);

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

    // ── Asks (SessionAsks), catalog (SessionCatalog), history (SessionHistory) ──

    public Task<Result<Unit>> AnswerQuestionAsync(
        string id,
        string requestId,
        IReadOnlyList<IReadOnlyList<string>> answers,
        CancellationToken ct = default)
        => Asks.AnswerQuestionAsync(id, requestId, answers, ct);

    public Task<Result<Unit>> RejectQuestionAsync(string id, string requestId, CancellationToken ct = default)
        => Asks.RejectQuestionAsync(id, requestId, ct);

    /// <inheritdoc cref="SessionAsks.ReplyToPermissionAsync"/>
    public Task<Result<Unit>> ReplyToPermissionAsync(
        string id,
        string requestId,
        string reply,
        string? message,
        CancellationToken ct = default)
        => Asks.ReplyToPermissionAsync(id, requestId, reply, message, ct);

    public Task<Result<IReadOnlyList<ProviderInfo>>> GetSessionModelsAsync(string sessionId, CancellationToken ct = default)
        => Catalog.GetSessionModelsAsync(sessionId, ct);

    public Task<Result<IReadOnlyList<CommandInfo>>> GetSessionCommandsAsync(string sessionId, CancellationToken ct = default)
        => Catalog.GetSessionCommandsAsync(sessionId, ct);

    public Task<Result<IReadOnlyList<AgentInfo>>> GetSessionAgentsAsync(string sessionId, CancellationToken ct = default)
        => Catalog.GetSessionAgentsAsync(sessionId, ct);

    public Task<Result<MessagePage>> GetSessionMessagesAsync(string id, MessageQuery? query = null, CancellationToken ct = default)
        => History.GetSessionMessagesAsync(id, query, ct);

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

    // ── Compaction, shell commands, work (SessionCompaction, SessionShellCommands, SessionWork) ──

    /// <inheritdoc cref="SessionCompaction.CompactAsync"/>
    public Task<Result<Unit>> CompactAsync(string id, CancellationToken ct = default)
        => Compaction.CompactAsync(id, ct);

    /// <inheritdoc cref="SessionShellCommands.RunShellCommandAsync"/>
    public Task<Result<Unit>> RunShellCommandAsync(string id, string? command, CancellationToken ct = default)
        => ShellCommands.RunShellCommandAsync(id, command, ct);

    /// <inheritdoc cref="SessionWork.StopWorkAsync"/>
    public Task<Result<RunningWorkItem>> StopWorkAsync(string sessionId, string itemId, CancellationToken ct = default)
        => Work.StopWorkAsync(sessionId, itemId, ct);

    /// <inheritdoc cref="SessionWork.ReadWorkOutputAsync"/>
    public Task<Result<WorkOutput>> ReadWorkOutputAsync(string sessionId, string itemId, long offset, CancellationToken ct = default)
        => Work.ReadWorkOutputAsync(sessionId, itemId, offset, ct);

    private string HarnessDisplayName(Session session)
        => harnessRegistry.GetByType(session.HarnessType)?.DisplayName ?? session.HarnessType;

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

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<Result<Session>> GetSessionAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        return session;
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
