using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Skills;
using WeaveFleet.Application.Terminals;
using WeaveFleet.Application.Tools;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Analytics;
using WeaveFleet.Infrastructure.Browser;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;
using WeaveFleet.Infrastructure.Harnesses.Pi;
using WeaveFleet.Infrastructure.Plugins;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.SessionSources;
using WeaveFleet.Infrastructure.Skills;
using WeaveFleet.Infrastructure.Terminals;
using WeaveFleet.Infrastructure.Tools;

namespace WeaveFleet.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services with the DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddBuiltInPlugin<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPlugin>(this IServiceCollection services)
        where TPlugin : class, IBackendPlugin
    {
        services.AddSingleton<TPlugin>();
        services.AddSingleton<IBackendPlugin>(serviceProvider => serviceProvider.GetRequiredService<TPlugin>());
        return services;
    }

    /// <summary>
    /// Adds the startup service that imports legacy local sessions when the host enables it.
    /// </summary>
    public static IServiceCollection AddLegacySessionImportStartupService(this IServiceCollection services)
    {
        services.AddHostedService<LegacySessionImportStartupService>();
        return services;
    }

    /// <summary>
    /// Adds the startup service that self-heals the launcher script if running in installed layout.
    /// Must run before OpenCode warmup so the launcher is patched before any child processes spawn.
    /// </summary>
    public static IServiceCollection AddLauncherPatchStartupService(this IServiceCollection services)
    {
        services.AddHostedService<LauncherPatchService>();
        return services;
    }

    /// <summary>
    /// Adds the best-effort startup warmup service for the pooled OpenCode harness.
    /// In auth-enabled mode the service no-ops immediately; in local mode it pre-warms
    /// one pooled process using the deterministic <c>"local-user"</c> owner identity.
    /// Must be registered after startup recovery so the database is in a consistent state.
    /// Post-login and post-preference warmup is covered by the runtime warmup API
    /// (<c>POST /api/harnesses/opencode/warmup</c>).
    /// </summary>
    public static IServiceCollection AddOpenCodeWarmupStartupService(this IServiceCollection services)
    {
        services.AddHostedService<OpenCodeWarmupHostedService>();
        return services;
    }

    /// <summary>
    /// Adds the bundled skills deployment service that ensures bundled skills are registered
    /// in the manifest and synced to harness paths on startup.
    /// In auth-enabled mode the service no-ops immediately; in local mode it deploys bundled
    /// skills to the deterministic <c>"local-user"</c> owner identity.
    /// Must be registered after skill infrastructure is available.
    /// </summary>
    public static IServiceCollection AddBundledSkillsStartupService(this IServiceCollection services)
    {
        services.AddHostedService<BundledSkillsHostedService>();
        return services;
    }

    /// <summary>
    /// Adds the startup service that moves skills and tools installed by older Fleet versions to
    /// where OpenCode reads them. Local mode only. Register before the bundled skills service.
    /// </summary>
    public static IServiceCollection AddLegacyInstallMigrationStartupService(this IServiceCollection services)
    {
        services.AddHostedService<LegacyInstallMigrationHostedService>();
        return services;
    }

    /// <summary>
    /// Adds all infrastructure services (database, repositories, external clients) to the service collection.
    /// </summary>
    public static IServiceCollection AddFleetInfrastructure(
        this IServiceCollection services,
        FleetOptions options)
    {
        // Register FleetOptions so singleton services (e.g. OpenCodeHarness) can inject it.
        services.AddSingleton(options);

        // Database connection factory (singleton — thread-safe, creates new connections per call)
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(options));

        // Migration runner (singleton)
        services.AddSingleton<MigrationRunner>();

        // Repositories (scoped — one per request)
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
        services.AddScoped<ISessionSourceUsageRepository, SessionSourceUsageRepository>();
        services.AddScoped<IInstanceRepository, InstanceRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IHarnessProfileRepository, HarnessProfileRepository>();
        services.AddScoped<IWeaveConfigRepository, WeaveConfigRepository>();
        services.AddScoped<ISessionCallbackRepository, SessionCallbackRepository>();
        services.AddScoped<IDelegationRepository, DelegationRepository>();
        services.AddScoped<IWorkspaceRootRepository, WorkspaceRootRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<ISessionSnapshotBuilder, SessionSnapshotBuilder>();
        services.AddScoped<ISessionMessageProxy, OpenCodeSessionMessageProxy>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBoardRepository, BoardRepository>();
        services.AddScoped<ISmartLinkRepository, SmartLinkRepository>();
        services.AddScoped<ISessionProgressRepository, SessionProgressRepository>();
        services.AddScoped<ICanvasRepository, CanvasRepository>();
        services.AddScoped<IAppRunRepository, AppRunRepository>();
        services.AddScoped<IAutomationRepository, AutomationRepository>();
        services.AddScoped<IAutomationEventLedgerRepository, AutomationEventLedgerRepository>();
        services.AddScoped<IAutomationRunRepository, AutomationRunRepository>();

        // Credential storage — user-scoped repositories and application services
        services.AddScoped<IUserPreferenceRepository, DapperUserPreferenceRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<ICredentialStore, CredentialStore>();
        services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();

        // Application services (scoped)
        services.AddScoped<ProjectService>();
        services.AddScoped<SessionService>();
        services.AddScoped<WeaveFleet.Application.Progress.SessionProgressReader>();
        services.AddScoped<WeaveFleet.Application.Services.Worktrees.WorktreeNamingService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceRootService>();
        services.AddScoped<InstanceService>();
        services.AddScoped<SessionSourceResolutionService>();
        services.AddScoped<GitDiffService>();
        services.AddScoped<SessionOrchestrator>();
        services.AddScoped<HarnessProfileService>();
        services.AddScoped<WeaveFleet.Application.Weave.WeaveConfigService>();
        services.AddSingleton<WeaveFleet.Application.Weave.WeaveDetectionCache>();
        services.AddSingleton<IBuiltInSkillCatalog, WeaveFleet.Infrastructure.Harnesses.OpenCode.OpenCodeBuiltInSkillCatalog>();
        services.AddScoped<BuiltInSkillService>();
        services.AddScoped<ISessionActivator>(sp => sp.GetRequiredService<SessionOrchestrator>());
        services.AddScoped<SessionCallbackService>();
        services.AddScoped<DelegationService>();
        services.AddScoped<SmartLinkService>();
        services.AddScoped<ICanvasService, CanvasService>();
        services.AddScoped<CanvasBridge>();
        services.AddScoped<SessionMessagesFeature>();
        services.AddScoped<SessionMessageBridge>();
        services.AddScoped<ISessionUpdateSender, SessionUpdateSender>();
        // Singleton: holds which messages a session asked to hear back about, until the turn handling them ends.
        services.AddSingleton<SessionUpdates>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowsFeature>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowModelRoles>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowService>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowDrafter>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowSkills>();
        services.AddScoped<WeaveFleet.Application.Workflows.IAutomationWorkflows, WeaveFleet.Application.Workflows.AutomationWorkflows>();
        services.AddScoped<WeaveFleet.Application.Workflows.WorkflowStepBridge>();
        services.AddScoped<WeaveFleet.Application.Workflows.IWorkflowStepSessions, WeaveFleet.Application.Workflows.WorkflowStepSessions>();
        services.AddScoped<WeaveFleet.Application.Workflows.IWorkflowRunEvents, WeaveFleet.Application.Workflows.WorkflowRunEvents>();
        services.AddSingleton<WeaveFleet.Application.Workflows.IWorkflowFiles, WeaveFleet.Application.Workflows.WorkflowFiles>();
        services.AddScoped<IWorkflowRunRepository, WorkflowRunRepository>();
        // Singleton: holds each run's lock and which step sessions it's watching for the end of their turn.
        services.AddSingleton<WeaveFleet.Application.Workflows.WorkflowRunner>();
        services.AddHostedService<WeaveFleet.Application.Workflows.WorkflowRunRecovery>();
        services.AddSingleton<IPtyFactory, PortaPtyFactory>();
        services.AddSingleton<ITerminalHistoryStore>(sp => new TerminalHistoryStore(sp.GetRequiredService<FleetOptions>().ResolvedTerminalHistoryDirectory));
        services.AddSingleton<TerminalManager>();
        services.AddSingleton<ISessionTerminalCleanup>(sp => sp.GetRequiredService<TerminalManager>());
        services.AddScoped<TerminalService>();
        services.AddHostedService<TerminalShutdownService>();
        services.AddSingleton<AppRunner>();
        services.AddSingleton<IAppRunner>(sp => sp.GetRequiredService<AppRunner>());
        services.AddSingleton<ISessionAppCleanup>(sp => sp.GetRequiredService<AppRunner>());
        services.AddScoped<AppRunService>();
        services.AddScoped<AppRunRecorder>();
        services.AddHostedService<AppRunRecorderService>();
        services.AddSingleton<IScreenshotter, HeadlessChromeScreenshotter>();
        services.AddSingleton(sp => new SessionScreenshotStore(
            sp.GetRequiredService<FleetOptions>().ResolvedScreenshotDirectory,
            sp.GetRequiredService<ILogger<SessionScreenshotStore>>()));
        services.AddSingleton<ISessionScreenshotStore>(sp => sp.GetRequiredService<SessionScreenshotStore>());
        services.AddHostedService<SessionScreenshotCleanupService>();
        services.AddHostedService<SideConversationSweeper>();
        services.AddScoped<BrowserPreviews>();
        services.AddScoped<BrowserBridge>();
        services.AddSingleton<IBackgroundUserScope, BackgroundUserScope>();
        services.AddScoped<AutomationService>();
        services.AddScoped<AutomationExecutionService>();
        services.AddScoped<IAutomationExecutor>(sp => sp.GetRequiredService<AutomationExecutionService>());
        services.AddScoped<AutomationRunService>();
        services.AddScoped<AutomationDraftService>();
        services.AddScoped<HarnessCatalogService>();
        services.AddScoped<HarnessSignInService>();
        services.AddSingleton<HarnessCatalogChanges>();
        services.AddScoped<EventTriggerMatcher>();
        services.AddScoped<SessionActivityWriteService>();
        services.AddScoped<ILegacySessionImporter, LegacySessionImporter>();
        services.AddScoped<UserService>();
        services.AddScoped<IBoardSyncService, BoardSyncService>();
        services.AddScoped<ISessionSourceProvider, LocalDirectorySessionSourceProvider>();
        services.AddSingleton<ISessionSourceProvider, RepositorySessionSourceProvider>();
        services.AddScoped<ISessionSourceProvider, GitHubSessionSourceProvider>();
        services.AddScoped<ISessionSourceProvider, AutomationSessionSourceProvider>();
        services.AddSingleton<ISessionSourceProvider, QuickChatSessionSourceProvider>();
        services.AddScoped<SystemUserContext>();


        // DirectoryService — scoped (depends on scoped WorkspaceRootService)
        services.AddScoped<DirectoryService>();

        // RepositoryService — singleton, owns the in-memory scan cache
        services.AddSingleton<RepositoryService>();

        // Integration store — singleton, file-backed
        services.AddSingleton<IIntegrationStore, FileIntegrationStore>();
        services.AddSingleton<IPluginStateStore, PluginStateStore>();
        services.AddSingleton<IPluginCatalog, BuiltInPluginCatalog>();

        // HttpClient factory for GitHub API calls
        services.AddHttpClient();
        services.AddHttpClient("GitHubApi", client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("fleet/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        });

        // Auto-update services
        services.AddSingleton<UpdateStateHolder>();
        services.AddSingleton<UpdateDownloadService>();
        services.AddSingleton<UpdateCheckService>();
        services.AddHostedService(sp => sp.GetRequiredService<UpdateCheckService>());

        // GitHub services — singleton
        services.AddScoped<GitHubService>();
        services.AddSingleton<GitHubApiProxy>();
        services.AddBuiltInPlugin<GitHubBackendPlugin>();

        // Smart links: the relay feeds the detector; the watcher stores, enriches and pushes them.
        services.AddSingleton<SmartLinkDetector>();
        services.AddSingleton<WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub.SmartLinkWatcherService>();
        services.AddSingleton<ISmartLinkWatcher>(sp => sp.GetRequiredService<WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub.SmartLinkWatcherService>());
        services.AddHostedService(sp => sp.GetRequiredService<WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub.SmartLinkWatcherService>());

        // Session progress: the relay feeds the observer; the service stores progress and pushes changes.
        services.AddSingleton<WeaveFleet.Infrastructure.Progress.SessionProgressObserver>();
        services.AddSingleton<WeaveFleet.Application.Progress.ISessionProgressObserver>(sp => sp.GetRequiredService<WeaveFleet.Infrastructure.Progress.SessionProgressObserver>());
        services.AddHostedService<WeaveFleet.Infrastructure.Progress.SessionProgressService>();

        // Where harnesses read skills, tools and config: ~/.config/opencode, <repo>/.opencode, …
        services.AddSingleton(_ => HarnessInstallPaths.FromEnvironment());

        // Skill services
        services.AddSingleton<ISkillCatalogService, GitHubSkillCatalogService>();
        services.AddSingleton<ISkillManifestStore, JsonSkillManifestStore>();
        services.AddSingleton<ISkillSyncEngine>(sp => new SkillSyncEngine(
            sp.GetRequiredService<ISkillManifestStore>(),
            sp.GetRequiredService<HarnessInstallPaths>(),
            sp.GetRequiredService<ILogger<SkillSyncEngine>>(),
            sp.GetService<IHarnessPoolRecycler>()));
        services.AddSingleton<IGitHubSkillFetcher, GitHubSkillFetcher>();
        services.AddSingleton<SkillManifestMigrator>();

        // Tool services
        services.AddSingleton<IToolCatalogService, GitHubToolCatalogService>();
        services.AddSingleton<IToolManifestStore, JsonToolManifestStore>();
        services.AddSingleton<IToolInstaller>(sp => new ToolInstaller(
            sp.GetRequiredService<HarnessInstallPaths>(),
            sp.GetRequiredService<ILogger<ToolInstaller>>(),
            sp.GetService<IHarnessPoolRecycler>()));

        // InstanceTracker is singleton — holds live in-process handles across requests
        services.AddSingleton<InstanceTracker>();

        // SessionActivityTracker is singleton — tracks ephemeral busy/idle state per session
        // for initial-state snapshots on WebSocket subscribe (page refresh support).
        services.AddSingleton<SessionActivityTracker>();
        services.AddSingleton<SessionCapabilitiesResolver>();

        // Which tabs are looking at which session: the recap waits on it, notifications keep quiet about it.
        services.AddSingleton<SessionFocusTracker>();

        // SessionRecapService is singleton — owns per-session recap timers.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IRecapPreference, RecapPreference>();
        services.AddSingleton<SessionRecapService>();

        // SessionNotifier is singleton — remembers each session's last activity status between events.
        services.AddSingleton<INotificationPreference, NotificationPreference>();
        services.AddSingleton<SessionNotifier>();

        // EventBroadcaster is singleton — pub/sub hub shared across all requests
        services.AddSingleton<IEventBroadcaster, InMemoryEventBroadcaster>();
        services.AddTransient<DomainEventTranslator>();
        services.AddSingleton<InProcessOutboxDispatcher>();
        services.AddSingleton<IOutboxDispatcher>(sp => sp.GetRequiredService<InProcessOutboxDispatcher>());

        // In-process event bus — pure .NET, no child process.
        services.AddInProcessEventBus();


        // HarnessEventRelay is transport-agnostic (depends on IEventPublisher only).
        services.AddHostedService<HarnessEventRelay>();

        // InstanceShutdownService stops all tracked harness instances on shutdown.
        // Registered after HarnessEventRelay so it stops first (reverse order), giving
        // relay pumps a chance to observe process exit and flush buffered deltas.
        services.AddHostedService<InstanceShutdownService>();

        services.AddHostedService<OutboxDispatchBackgroundService>();
        services.AddHostedService<OutboxCleanupBackgroundService>();

        // Automation background services
        services.AddHostedService<AutomationSchedulerService>();
        services.AddHostedService<AutomationEventDispatcherService>();

        // ── Analytics ─────────────────────────────────────────────────────────
        if (options.AnalyticsEnabled)
        {
            // Analytics connection factory (singleton — one pool per process)
            var analyticsDbPath = options.ResolvedAnalyticsDatabasePath;
            services.AddSingleton<IAnalyticsDbConnectionFactory>(
                _ => new AnalyticsSqliteConnectionFactory(analyticsDbPath));

            // Analytics migration runner (singleton — applied once at startup)
            services.AddSingleton<AnalyticsMigrationRunner>();

            // Analytics collector (singleton — owns the bounded channel)
            services.AddSingleton<AnalyticsCollector>();
            services.AddSingleton<IAnalyticsCollector>(sp => sp.GetRequiredService<AnalyticsCollector>());

            // Analytics repository (scoped — follows repository pattern)
            services.AddScoped<IAnalyticsReader, AnalyticsRepository>();

            // Background services (same pattern as HarnessEventRelay)
            services.AddHostedService<AnalyticsWriterService>();
            services.AddHostedService<AnalyticsRollupService>();
        }
        else
        {
            // When analytics disabled, provide a no-op collector so DI never fails
            services.AddSingleton<IAnalyticsCollector, NullAnalyticsCollector>();
        }

        // HarnessRegistry is Singleton — any IHarness registrations MUST also be
        // Singleton to avoid a captive-dependency runtime failure.
        services.AddSingleton<IHarnessRegistry, HarnessRegistry>();
        services.AddSingleton<IHarnessUpdateService>(sp => new HarnessUpdateService(
            sp.GetRequiredService<IHarnessRegistry>(),
            sp.GetRequiredService<SessionActivityTracker>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<HarnessUpdateService>>()));

        // OpenCode harness — singleton to match HarnessRegistry lifetime.
        // PortAllocator is a standalone singleton seeded from FleetOptions.
        services.AddSingleton(new PortAllocator(options.HarnessPortRangeStart, options.HarnessPortRangeEnd));

        // Named HttpClient used by OpenCodeHarnessRuntime.SpawnAsync to create per-instance clients.
        services.AddHttpClient("OpenCode");

        // Register OpenCodeHarness (descriptor) and OpenCodeHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<OpenCodeHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<OpenCodeHarness>());
        services.AddSingleton<OpenCodeHarnessRuntime>(sp => new OpenCodeHarnessRuntime(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PortAllocator>(),
            sp.GetRequiredService<FleetOptions>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<OpenCodeHarnessRuntime>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<IAnalyticsCollector>(),
            sp.GetRequiredService<IEventBroadcaster>(),
            sp.GetRequiredService<SessionActivityTracker>()));
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<OpenCodeHarnessRuntime>());
        services.AddSingleton<IHarnessPoolRecycler>(sp => new OpenCodeHarnessPoolRecycler(sp.GetRequiredService<OpenCodeHarnessRuntime>()));
        services.AddSingleton<IOpenCodePoolHealthCheck, PoolHealthCheck>();
        services.AddSingleton<IHarnessCanvasCallerResolver>(sp => new OpenCodeCanvasCallerResolver(
            sp.GetRequiredService<OpenCodeHarnessRuntime>(),
            sp.GetRequiredService<ILogger<OpenCodeCanvasCallerResolver>>()));
        services.AddSingleton<IHarnessBridgeTokens>(sp => new OpenCodeBridgeTokens(sp.GetRequiredService<OpenCodeHarnessRuntime>()));

        // Register ClaudeCodeHarness (descriptor) and ClaudeCodeHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<ClaudeCodeHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<ClaudeCodeHarness>());
        services.AddSingleton<ClaudeCodeHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<ClaudeCodeHarnessRuntime>());

        // Register PiHarness (descriptor) and PiHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<PiHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<PiHarness>());
        services.AddSingleton<PiHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<PiHarnessRuntime>());

        // OpenCode 2: a harness of its own next to OpenCode, off until the user turns it on (opencode2.enabled).
        services.AddHttpClient(OpenCode2HarnessRuntime.HttpClientName);
        services.AddHttpClient(OpenCode2HarnessRuntime.SignInCallbackHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
            .RemoveAllLoggers();
        services.AddSingleton<OpenCode2Harness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<OpenCode2Harness>());
        services.AddSingleton<OpenCode2HarnessRuntime>(sp => new OpenCode2HarnessRuntime(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<FleetOptions>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<OpenCode2HarnessRuntime>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetService<IAnalyticsCollector>()));
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<OpenCode2HarnessRuntime>());
        services.AddSingleton<IHarnessBridgeTokens>(sp => new OpenCode2BridgeTokens(sp.GetRequiredService<OpenCode2HarnessRuntime>()));
        services.AddSingleton<IHarnessCanvasCallerResolver>(sp => new OpenCode2CanvasCallerResolver(
            sp.GetRequiredService<OpenCode2HarnessRuntime>(),
            sp.GetRequiredService<ILogger<OpenCode2CanvasCallerResolver>>()));

        return services;
    }
}
