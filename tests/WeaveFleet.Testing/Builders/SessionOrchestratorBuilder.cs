using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Sessions;
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
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Testing.Builders;

/// <summary>
/// Test builder that constructs a <see cref="SessionOrchestrator"/> with all dependencies
/// defaulting to in-memory fakes. Individual dependencies can be overridden via fluent methods.
/// Eliminates the 12-17 line mock setup blocks duplicated across test files.
/// </summary>
public sealed class SessionOrchestratorBuilder
{
    // ── Exposed fakes for seeding and assertion ──────────────────────────────

    public InMemorySessionRepository SessionRepository { get; } = new();
    public InMemorySessionSourceUsageRepository SessionSourceUsageRepository { get; } = new();
    public InMemorySessionCallbackRepository SessionCallbackRepository { get; } = new();
    public InMemoryDelegationRepository DelegationRepository { get; } = new();
    public InMemoryProjectRepository ProjectRepository { get; } = new();
    public InMemoryWorkspaceRepository WorkspaceRepository { get; } = new();
    public InMemoryWorkspaceRootRepository WorkspaceRootRepository { get; } = new();
    public InMemoryInstanceRepository InstanceRepository { get; } = new();
    public InMemoryMessageRepository MessageRepository { get; } = new();
    public InMemoryOutboxRepository OutboxRepository { get; } = new();
    public InMemorySmartLinkRepository SmartLinkRepository { get; } = new();
    public FakeEventBroadcaster EventBroadcaster { get; } = new();
    public FakeAnalyticsCollector AnalyticsCollector { get; } = new();
    public FakeCredentialStore CredentialStore { get; } = new();
    public FakeHarnessRegistry HarnessRegistry { get; } = new();
    public InMemoryUserPreferenceRepository UserPreferenceRepository { get; } = new();
    public InstanceTracker InstanceTracker { get; } = new();
    public SessionActivityTracker ActivityTracker { get; } = new();
    public FakeSessionMessageProxy SessionMessageProxy { get; } = new();
    public InMemoryHarnessProfileRepository HarnessProfileRepository { get; }

    // ── The session services Build() made, for tests (and DI overrides) that use them directly ──

    public SessionActivation Activation { get; private set; } = null!;
    public SessionPrompting Prompting { get; private set; } = null!;
    public SessionAsks Asks { get; private set; } = null!;
    public SessionCatalog Catalog { get; private set; } = null!;
    public SessionHistory History { get; private set; } = null!;
    public SessionCompaction Compaction { get; private set; } = null!;
    public SessionShellCommands ShellCommands { get; private set; } = null!;
    public SessionWork Work { get; private set; } = null!;
    public SessionCreation Creation { get; private set; } = null!;
    public SessionRetention Retention { get; private set; } = null!;
    public SessionForking Forking { get; private set; } = null!;
    public SessionSideConversations SideConversations { get; private set; } = null!;
    public SessionFiles Files { get; private set; } = null!;

    public SessionOrchestratorBuilder()
    {
        HarnessProfileRepository = new InMemoryHarnessProfileRepository(SessionRepository);
    }

    // ── Overridable dependencies ─────────────────────────────────────────────

    private IUserContext _userContext = new TestUserContext("test-user");
    private FleetOptions _options = new();
    private GitDiffService? _gitDiffService;
    private ISessionAppCleanup? _sessionApps;
    private ISessionScreenshotStore? _sessionScreenshots;
    private ISessionTerminalCleanup? _sessionTerminals;
    private WeaveFleet.Application.Pages.IPageStore? _sessionPages;
    private AgentMemoryService? _agentMemory;
    private readonly List<ISessionSourceProvider> _additionalSourceProviders = [];

    /// <summary>Adds a session source provider beyond the local-directory one Build() always registers.</summary>
    public SessionOrchestratorBuilder WithSessionSourceProvider(ISessionSourceProvider provider)
    {
        _additionalSourceProviders.Add(provider);
        return this;
    }

    public SessionOrchestratorBuilder WithUserContext(IUserContext userContext)
    {
        _userContext = userContext;
        return this;
    }

    public SessionOrchestratorBuilder WithOptions(FleetOptions options)
    {
        _options = options;
        return this;
    }

    public SessionOrchestratorBuilder WithGitDiffService(GitDiffService gitDiffService)
    {
        _gitDiffService = gitDiffService;
        return this;
    }

    public SessionOrchestratorBuilder WithSessionApps(ISessionAppCleanup sessionApps)
    {
        _sessionApps = sessionApps;
        return this;
    }

    public SessionOrchestratorBuilder WithSessionTerminals(ISessionTerminalCleanup sessionTerminals)
    {
        _sessionTerminals = sessionTerminals;
        return this;
    }

    public SessionOrchestratorBuilder WithSessionPages(WeaveFleet.Application.Pages.IPageStore sessionPages)
    {
        _sessionPages = sessionPages;
        return this;
    }

    public SessionOrchestratorBuilder WithSessionScreenshots(ISessionScreenshotStore sessionScreenshots)
    {
        _sessionScreenshots = sessionScreenshots;
        return this;
    }

    public SessionOrchestratorBuilder WithAgentMemory(AgentMemoryService agentMemory)
    {
        _agentMemory = agentMemory;
        return this;
    }

    /// <summary>
    /// Registers a harness and its runtime in the registry and returns the runtime for further configuration.
    /// </summary>
    public FakeHarnessRuntime RegisterHarness(string harnessType, string displayName = "Test Harness", HarnessCapabilities? capabilities = null)
    {
        var harness = new FakeHarness(harnessType, displayName, capabilities);
        var runtime = new FakeHarnessRuntime(harnessType);
        HarnessRegistry.Register(harness);
        HarnessRegistry.Register(runtime);
        return runtime;
    }

    // ── Build ────────────────────────────────────────────────────────────────

    public SessionOrchestrator Build()
    {
        var workspaceRootService = new WorkspaceRootService(WorkspaceRootRepository, _userContext);
        var workspaceService = new WorkspaceService(
            WorkspaceRepository,
            _userContext,
            _options,
            NullLogger<WorkspaceService>.Instance);

        var instanceService = new InstanceService(InstanceRepository, SessionRepository, _userContext);
        var sessionSourceResolutionService = new SessionSourceResolutionService([
            new LocalDirectorySessionSourceProvider(workspaceRootService),
            .. _additionalSourceProviders,
        ]);

        var delegationService = new DelegationService(DelegationRepository, EventBroadcaster, _userContext);

        // Wire up the fake proxy to use the message repository for fallback behavior
        SessionMessageProxy.GetMessagesBehavior ??= async (sessionId, limit, before, ct) =>
        {
            var effectiveLimit = limit ?? 100;
            var rows = await MessageRepository.GetBySessionAsync(sessionId, effectiveLimit + 1, before);
            var hasMore = rows.Count > effectiveLimit;
            var pageRows = hasMore ? rows.Skip(rows.Count - effectiveLimit).ToList() : rows;
            var messages = MessagePersistenceService.ToHarnessMessages(pageRows);
            return new MessagePage(messages, hasMore);
        };

        var harnessAvailability = new HarnessAvailabilityCache(
            HarnessRegistry, TimeProvider.System, NullLogger<HarnessAvailabilityCache>.Instance);
        var gitDiffService = _gitDiffService ?? new GitDiffService();

        Activation = new SessionActivation(
            workspaceService, instanceService, HarnessRegistry, InstanceTracker, SessionRepository, ProjectRepository,
            EventBroadcaster, CredentialStore, UserPreferenceRepository, NullLogger<SessionActivation>.Instance,
            HarnessProfileRepository);
        Prompting = new SessionPrompting(
            SessionRepository, HarnessRegistry, Activation, ActivityTracker, delegationService, sessionSourceResolutionService,
            SessionSourceUsageRepository, EventBroadcaster, _userContext, NullLogger<SessionPrompting>.Instance,
            MessageRepository, agentMemory: _agentMemory);
        Asks = new SessionAsks(SessionRepository, InstanceTracker, Activation, NullLogger<SessionAsks>.Instance);
        Catalog = new SessionCatalog(SessionRepository, Activation, NullLogger<SessionCatalog>.Instance);
        History = new SessionHistory(SessionRepository, SessionMessageProxy, _options, NullLogger<SessionHistory>.Instance);
        Compaction = new SessionCompaction(SessionRepository, HarnessRegistry, ActivityTracker, Activation, NullLogger<SessionCompaction>.Instance);
        ShellCommands = new SessionShellCommands(SessionRepository, HarnessRegistry, Activation, NullLogger<SessionShellCommands>.Instance);
        Work = new SessionWork(SessionRepository, InstanceTracker, delegationService);
        Creation = new SessionCreation(
            _options, workspaceService, instanceService, sessionSourceResolutionService, HarnessRegistry, InstanceTracker,
            SessionRepository, SessionSourceUsageRepository, SessionCallbackRepository, ProjectRepository, EventBroadcaster,
            AnalyticsCollector, CredentialStore, UserPreferenceRepository, _userContext, Activation, Prompting,
            NullLogger<SessionCreation>.Instance, gitDiffService: gitDiffService, harnessProfiles: HarnessProfileRepository,
            harnessAvailability: harnessAvailability);
        Retention = new SessionRetention(
            workspaceService, instanceService, InstanceTracker, SessionRepository, DelegationRepository, SmartLinkRepository,
            EventBroadcaster, AnalyticsCollector, ActivityTracker, NullLogger<SessionRetention>.Instance,
            sessionTerminals: _sessionTerminals, sessionApps: _sessionApps, sessionScreenshots: _sessionScreenshots,
            sessionPages: _sessionPages);
        Forking = new SessionForking(
            workspaceService, instanceService, HarnessRegistry, InstanceTracker, SessionRepository, ProjectRepository,
            EventBroadcaster, AnalyticsCollector, CredentialStore, Activation, NullLogger<SessionForking>.Instance, gitDiffService);
        SideConversations = new SessionSideConversations(
            workspaceService, HarnessRegistry, InstanceTracker, SessionRepository, ProjectRepository, EventBroadcaster,
            AnalyticsCollector, Prompting, Forking, Retention, NullLogger<SessionSideConversations>.Instance);
        Files = new SessionFiles(SessionRepository, EventBroadcaster, NullLogger<SessionFiles>.Instance);

        return new SessionOrchestrator(
            workspaceService,
            instanceService,
            sessionSourceResolutionService,
            HarnessRegistry,
            InstanceTracker,
            SessionRepository,
            SessionSourceUsageRepository,
            SessionCallbackRepository,
            DelegationRepository,
            ProjectRepository,
            EventBroadcaster,
            AnalyticsCollector,
            SessionMessageProxy,
            delegationService,
            CredentialStore,
            UserPreferenceRepository,
            _userContext,
            _options,
            SmartLinkRepository,
            ActivityTracker,
            sessionActivityWriteService: null,
            gitDiffService: gitDiffService,
            sessionApps: _sessionApps,
            messageRepository: MessageRepository,
            harnessProfiles: HarnessProfileRepository,
            sessionScreenshots: _sessionScreenshots,
            sessionTerminals: _sessionTerminals,
            sessionPages: _sessionPages,
            agentMemory: _agentMemory,
            harnessAvailability: harnessAvailability,
            sessionFiles: Files,
            sessionActivation: Activation,
            sessionPrompting: Prompting,
            sessionAsks: Asks,
            sessionCatalog: Catalog,
            sessionHistory: History,
            sessionCompaction: Compaction,
            sessionShellCommands: ShellCommands,
            sessionWork: Work,
            sessionCreation: Creation,
            sessionRetention: Retention,
            sessionForking: Forking,
            sessionSideConversations: SideConversations);
    }
}
