using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Credentials;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions.Activation;
using WeaveFleet.Application.Sessions.Asks;
using WeaveFleet.Application.Sessions.Catalog;
using WeaveFleet.Application.Sessions.Compaction;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Application.Sessions.Files;
using WeaveFleet.Application.Sessions.Forking;
using WeaveFleet.Application.Sessions.History;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.Sessions.Retention;
using WeaveFleet.Application.Sessions.Shell;
using WeaveFleet.Application.Sessions.Side;
using WeaveFleet.Application.Sessions.Work;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Users;
using WeaveFleet.Application.Workspaces;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Every session operation behind one object, each delegated to the service that owns it (<see cref="SessionCreation"/>,
/// <see cref="SessionActivation"/>, <see cref="SessionPrompting"/>, <see cref="SessionRetention"/> and the rest under
/// <c>Application/Sessions</c>). Fleet's own code uses those services; tests and live-test fixtures still drive sessions
/// through this facade.
/// </summary>
public sealed class SessionOrchestrator(
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
    SessionCreation? sessionCreation = null,
    SessionRetention? sessionRetention = null,
    SessionForking? sessionForking = null,
    SessionSideConversations? sessionSideConversations = null) : ISessionActivator
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
    private SessionRetention? _retention;
    private SessionForking? _forking;
    private SessionSideConversations? _sideConversations;

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

    private SessionRetention Retention => _retention ??= sessionRetention
        ?? new SessionRetention(
            workspaceService,
            instanceService,
            instanceTracker,
            sessionRepository,
            delegationRepository,
            smartLinkRepository,
            eventBroadcaster,
            analyticsCollector,
            sessionActivityTracker,
            NullLogger<SessionRetention>.Instance,
            sessionActivityWriteService,
            sessionTerminals,
            sessionApps,
            sessionRecaps,
            sessionNotifier,
            sessionScreenshots,
            sessionPages);

    private SessionForking Forking => _forking ??= sessionForking
        ?? new SessionForking(
            workspaceService,
            instanceService,
            harnessRegistry,
            instanceTracker,
            sessionRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            credentialStore,
            Activation,
            NullLogger<SessionForking>.Instance,
            _gitDiffService);

    private SessionSideConversations SideConversations => _sideConversations ??= sessionSideConversations
        ?? new SessionSideConversations(
            workspaceService,
            harnessRegistry,
            instanceTracker,
            sessionRepository,
            projectRepository,
            eventBroadcaster,
            analyticsCollector,
            Prompting,
            Forking,
            Retention,
            NullLogger<SessionSideConversations>.Instance);

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
        ISmartLinkRepository smartLinkRepository)
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
        ISmartLinkRepository smartLinkRepository)
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
            sessionActivityWriteService: null)
    {
    }


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

    // ── Side conversations (SessionSideConversations) ──────────────────────────

    /// <inheritdoc cref="SessionSideConversations.GetSideConversationAsync"/>
    public Task<Result<Session?>> GetSideConversationAsync(string sessionId)
        => SideConversations.GetSideConversationAsync(sessionId);

    /// <inheritdoc cref="SessionSideConversations.AskSideQuestionAsync"/>
    public Task<Result<SideQuestionResult>> AskSideQuestionAsync(
        string sessionId,
        string? question,
        PromptOptions? options,
        string? correlationId,
        CancellationToken ct = default)
        => SideConversations.AskSideQuestionAsync(sessionId, question, options, correlationId, ct);

    /// <inheritdoc cref="SessionSideConversations.CloseSideConversationAsync"/>
    public Task<Result<Unit>> CloseSideConversationAsync(string sessionId, CancellationToken ct = default)
        => SideConversations.CloseSideConversationAsync(sessionId, ct);

    /// <inheritdoc cref="SessionSideConversations.RestoreSideConversationAsync"/>
    public Task<Result<Session>> RestoreSideConversationAsync(string sessionId, CancellationToken ct = default)
        => SideConversations.RestoreSideConversationAsync(sessionId, ct);

    /// <inheritdoc cref="SessionSideConversations.GetUndoableSideConversationAsync"/>
    public Task<Result<(Session SideConversation, TimeSpan UndoLeft)?>> GetUndoableSideConversationAsync(string sessionId)
        => SideConversations.GetUndoableSideConversationAsync(sessionId);

    /// <inheritdoc cref="SessionSideConversations.SetSideConversationSeenAsync"/>
    public Task<Result<Session>> SetSideConversationSeenAsync(string sessionId, string? answerId)
        => SideConversations.SetSideConversationSeenAsync(sessionId, answerId);

    /// <inheritdoc cref="SessionSideConversations.SetSideConversationMinimizedAsync"/>
    public Task<Result<Session>> SetSideConversationMinimizedAsync(string sessionId, bool minimized, CancellationToken ct = default)
        => SideConversations.SetSideConversationMinimizedAsync(sessionId, minimized, ct);

    /// <inheritdoc cref="SessionSideConversations.DeleteDiscardedSideConversationAsync"/>
    public Task DeleteDiscardedSideConversationAsync(string sideSessionId, CancellationToken ct = default)
        => SideConversations.DeleteDiscardedSideConversationAsync(sideSessionId, ct);

    /// <inheritdoc cref="SessionSideConversations.KeepSideConversationAsync"/>
    public Task<Result<Session>> KeepSideConversationAsync(string sessionId, CancellationToken ct = default)
        => SideConversations.KeepSideConversationAsync(sessionId, ct);

    // ── Fork (SessionForking) ──────────────────────────────────────────────────

    /// <inheritdoc cref="SessionForking.ForkSessionAsync"/>
    public Task<Result<CreateSessionResult>> ForkSessionAsync(string parentId, string? title = null, CancellationToken ct = default)
        => Forking.ForkSessionAsync(parentId, title, ct);

    // ── Archive, restore, delete (SessionRetention) ────────────────────────────

    public Task<Result<Unit>> ArchiveSessionAsync(string id, CancellationToken ct = default)
        => Retention.ArchiveSessionAsync(id, ct);

    /// <inheritdoc cref="SessionRetention.UnarchiveSessionAsync"/>
    public Task<Result<Unit>> UnarchiveSessionAsync(string id, CancellationToken ct = default)
        => Retention.UnarchiveSessionAsync(id, ct);

    public Task<Result<Unit>> DeleteSessionAsync(string id, CancellationToken ct = default)
        => Retention.DeleteSessionAsync(id, ct);

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

}
