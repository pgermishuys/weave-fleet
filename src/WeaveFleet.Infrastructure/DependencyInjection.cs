using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Analytics;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Infrastructure.Plugins;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;
using WeaveFleet.Infrastructure.SessionSources;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services with the DI container.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddBuiltInPlugin<TPlugin>(this IServiceCollection services)
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
        // Enable snake_case → PascalCase column mapping for Dapper globally
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Register FleetOptions so singleton services (e.g. OpenCodeHarness) can inject it.
        services.AddSingleton(options);

        // Database connection factory (singleton — thread-safe, creates new connections per call)
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(options));

        // Migration runner (singleton)
        services.AddSingleton<MigrationRunner>();

        // Repositories (scoped — one per request)
        services.AddScoped<IProjectRepository, DapperProjectRepository>();
        services.AddScoped<IWorkspaceRepository, DapperWorkspaceRepository>();
        services.AddScoped<ISessionSourceUsageRepository, DapperSessionSourceUsageRepository>();
        services.AddScoped<IInstanceRepository, DapperInstanceRepository>();
        services.AddScoped<ISessionRepository, DapperSessionRepository>();
        services.AddScoped<ISessionCallbackRepository, DapperSessionCallbackRepository>();
        services.AddScoped<IDelegationRepository, DapperDelegationRepository>();
        services.AddScoped<IWorkspaceRootRepository, DapperWorkspaceRootRepository>();
        services.AddScoped<IMessageRepository, DapperMessageRepository>();
        services.AddScoped<IUserRepository, DapperUserRepository>();

        // Credential storage — user-scoped repositories and application services
        services.AddScoped<IUserCredentialRepository, DapperUserCredentialRepository>();
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
        services.AddScoped<UserService>();
        services.AddScoped<ISessionSourceProvider, LocalDirectorySessionSourceProvider>();
        services.AddSingleton<ISessionSourceProvider, ManagedWorkspaceSessionSourceProvider>();
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

        // GitHub services — singleton
        services.AddScoped<GitHubService>();
        services.AddSingleton<GitHubApiProxy>();
        services.AddBuiltInPlugin<GitHubBackendPlugin>();

        // InstanceTracker is singleton — holds live in-process handles across requests
        services.AddSingleton<InstanceTracker>();

        // EventBroadcaster is singleton — pub/sub hub shared across all requests
        services.AddSingleton<IEventBroadcaster, InMemoryEventBroadcaster>();

        // HarnessEventRelay bridges harness instance events to the broadcaster
        services.AddHostedService<HarnessEventRelay>();

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

        return services;
    }
}
