using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Analytics;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.Events;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Harnesses.NuCode;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Plugins;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;
using WeaveFleet.Infrastructure.Services;
using WeaveFleet.Infrastructure.SessionSources;

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
        services.AddScoped<ISessionCallbackRepository, SessionCallbackRepository>();
        services.AddScoped<IDelegationRepository, DelegationRepository>();
        services.AddScoped<IWorkspaceRootRepository, WorkspaceRootRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IHarnessEventLogRepository, HarnessEventLogRepository>();
        services.AddScoped<ISessionSnapshotBuilder, SessionSnapshotBuilder>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IBoardRepository, BoardRepository>();
        services.AddScoped<ISmartLinkRepository, SmartLinkRepository>();

        // Credential storage — user-scoped repositories and application services
        services.AddScoped<IUserPreferenceRepository, DapperUserPreferenceRepository>();
        services.AddScoped<IUserCredentialRepository, UserCredentialRepository>();
        services.AddScoped<ICredentialStore, CredentialStore>();
        services.AddScoped<ICredentialProtector, DataProtectionCredentialProtector>();

        // Application services (scoped)
        services.AddScoped<ProjectService>();
        services.AddScoped<SessionService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceRootService>();
        services.AddScoped<InstanceService>();
        services.AddScoped<SessionSourceResolutionService>();
        services.AddScoped<SessionOrchestrator>();
        services.AddScoped<SessionCallbackService>();
        services.AddScoped<DelegationService>();
        services.AddScoped<SmartLinkService>();
        services.AddScoped<SessionActivityWriteService>();
        services.AddScoped<UserService>();
        services.AddScoped<IBoardSyncService, BoardSyncService>();
        services.AddScoped<ISessionSourceProvider, LocalDirectorySessionSourceProvider>();
        services.AddSingleton<ISessionSourceProvider, RepositorySessionSourceProvider>();
        services.AddScoped<ISessionSourceProvider, GitHubSessionSourceProvider>();
        services.AddScoped<SystemUserContext>();

        // ConfigService — singleton, no DB dependency, file-based
        services.AddSingleton<ConfigService>();

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
        services.AddHostedService<WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub.CiWatcherService>();
        services.AddHostedService<WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub.ReviewCommentWatcherService>();

        // InstanceTracker is singleton — holds live in-process handles across requests
        services.AddSingleton<InstanceTracker>();

        // SessionActivityTracker is singleton — tracks ephemeral busy/idle state per session
        // for initial-state snapshots on WebSocket subscribe (page refresh support).
        services.AddSingleton<SessionActivityTracker>();

        // EventBroadcaster is singleton — pub/sub hub shared across all requests
        services.AddSingleton<IEventBroadcaster, InMemoryEventBroadcaster>();
        services.AddTransient<DomainEventTranslator>();
        services.AddSingleton<InProcessOutboxDispatcher>();
        services.AddSingleton<IOutboxDispatcher>(sp => sp.GetRequiredService<InProcessOutboxDispatcher>());

        // Shared text-delta buffer — singleton so fragments buffered by the fan-out service
        // (InProcessFanOutService) survive across scoped persister invocations.
        services.AddSingleton<TextDeltaBuffer>();

        // Harness event persister — scoped so message/session repositories flow through correctly.
        services.AddScoped<HarnessEventPersistenceService>();
        services.AddScoped<IHarnessEventPersister>(sp => sp.GetRequiredService<HarnessEventPersistenceService>());

        // In-process event bus — pure .NET, no child process.
        services.AddInProcessEventBus(bus =>
        {
            bus.AddProjection<WeaveFleet.Application.Projections.MessagePersistenceProjection>(ConsumerScope.Cluster);
        });

        // HarnessEventRelay is transport-agnostic (depends on IEventPublisher only).
        services.AddHostedService<HarnessEventRelay>();

        // InstanceShutdownService stops all tracked harness instances on shutdown.
        // Registered after HarnessEventRelay so it stops first (reverse order), giving
        // relay pumps a chance to observe process exit and flush buffered deltas.
        services.AddHostedService<InstanceShutdownService>();

        services.AddHostedService<OutboxDispatchBackgroundService>();
        services.AddHostedService<OutboxCleanupBackgroundService>();

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

        // OpenCode harness — singleton to match HarnessRegistry lifetime.
        // PortAllocator is a standalone singleton seeded from FleetOptions.
        services.AddSingleton(new PortAllocator(options.HarnessPortRangeStart, options.HarnessPortRangeEnd));

        // Named HttpClient used by OpenCodeHarnessRuntime.SpawnAsync to create per-instance clients.
        services.AddHttpClient("OpenCode");

        // Register OpenCodeHarness (descriptor) and OpenCodeHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<OpenCodeHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<OpenCodeHarness>());
        services.AddSingleton<OpenCodeHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<OpenCodeHarnessRuntime>());

        // Register ClaudeCodeHarness (descriptor) and ClaudeCodeHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<ClaudeCodeHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<ClaudeCodeHarness>());
        services.AddSingleton<ClaudeCodeHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<ClaudeCodeHarnessRuntime>());

        // Register NuCodeHarness (descriptor) and NuCodeHarnessRuntime (provisioning) as separate singletons.
        services.AddSingleton<NuCodeHarness>();
        services.AddSingleton<IHarness>(sp => sp.GetRequiredService<NuCodeHarness>());
        services.AddSingleton<NuCodeHarnessRuntime>();
        services.AddSingleton<IHarnessRuntime>(sp => sp.GetRequiredService<NuCodeHarnessRuntime>());
        services.AddScoped<INuCodeConnectionTester, NuCodeConnectionTester>();

        return services;
    }
}
