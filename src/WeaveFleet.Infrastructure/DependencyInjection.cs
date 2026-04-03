using Dapper;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Data;
using WeaveFleet.Infrastructure.Data.Repositories;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services with the DI container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds all infrastructure services (database, repositories, external clients) to the service collection.
    /// </summary>
    public static IServiceCollection AddFleetInfrastructure(
        this IServiceCollection services,
        FleetOptions options)
    {
        // Enable snake_case → PascalCase column mapping for Dapper globally
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        // Database connection factory (singleton — thread-safe, creates new connections per call)
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(options));

        // Migration runner (singleton)
        services.AddSingleton<MigrationRunner>();

        // Repositories (scoped — one per request)
        services.AddScoped<IProjectRepository, DapperProjectRepository>();
        services.AddScoped<IWorkspaceRepository, DapperWorkspaceRepository>();
        services.AddScoped<IInstanceRepository, DapperInstanceRepository>();
        services.AddScoped<ISessionRepository, DapperSessionRepository>();
        services.AddScoped<ISessionCallbackRepository, DapperSessionCallbackRepository>();
        services.AddScoped<IWorkspaceRootRepository, DapperWorkspaceRootRepository>();

        // Application services (scoped)
        services.AddScoped<ProjectService>();
        services.AddScoped<SessionService>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<WorkspaceRootService>();
        services.AddScoped<InstanceService>();
        services.AddScoped<SessionOrchestrator>();
        services.AddScoped<SessionCallbackService>();

        // ConfigService — singleton, no DB dependency, file-based
        services.AddSingleton<ConfigService>();

        // DirectoryService — scoped (depends on scoped WorkspaceRootService)
        services.AddScoped<DirectoryService>();

        // RepositoryService — singleton, owns the in-memory scan cache
        services.AddSingleton<RepositoryService>();

        // Integration store — singleton, file-backed
        services.AddSingleton<IIntegrationStore, FileIntegrationStore>();

        // HttpClient factory for GitHub API calls
        services.AddHttpClient();

        // GitHub services — singleton
        services.AddSingleton<GitHubService>();
        services.AddSingleton<GitHubApiProxy>();

        // InstanceTracker is singleton — holds live in-process handles across requests
        services.AddSingleton<InstanceTracker>();

        // EventBroadcaster is singleton — pub/sub hub shared across all requests
        services.AddSingleton<IEventBroadcaster, InMemoryEventBroadcaster>();

        // HarnessRegistry is Singleton — any IHarness registrations MUST also be
        // Singleton to avoid a captive-dependency runtime failure.
        services.AddSingleton<IHarnessRegistry, HarnessRegistry>();

        return services;
    }
}
