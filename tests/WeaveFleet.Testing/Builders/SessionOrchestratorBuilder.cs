using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
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
    public InMemoryHarnessEventLogRepository HarnessEventLogRepository { get; } = new();
    public InMemorySmartLinkRepository SmartLinkRepository { get; } = new();
    public FakeEventBroadcaster EventBroadcaster { get; } = new();
    public FakeAnalyticsCollector AnalyticsCollector { get; } = new();
    public FakeCredentialStore CredentialStore { get; } = new();
    public FakeHarnessRegistry HarnessRegistry { get; } = new();
    public InMemoryUserPreferenceRepository UserPreferenceRepository { get; } = new();
    public InstanceTracker InstanceTracker { get; } = new();

    // ── Overridable dependencies ─────────────────────────────────────────────

    private IUserContext _userContext = new TestUserContext("test-user");
    private FleetOptions _options = new();
    private GitDiffService? _gitDiffService;

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
            new LocalDirectorySessionSourceProvider(workspaceRootService)
        ]);

        var delegationService = new DelegationService(DelegationRepository, EventBroadcaster, _userContext);

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
            new FakeEventPublisher(),
            AnalyticsCollector,
            MessageRepository,
            HarnessEventLogRepository,
            delegationService,
            CredentialStore,
            UserPreferenceRepository,
            _userContext,
            _options,
            SmartLinkRepository,
            NullLogger<SessionOrchestrator>.Instance,
            sessionActivityWriteService: null,
            gitDiffService: _gitDiffService);
    }
}
