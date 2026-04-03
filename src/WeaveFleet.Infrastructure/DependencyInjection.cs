using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure;

/// <summary>
/// Extension methods for registering infrastructure services with the DI container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds all infrastructure services (database, repositories, external clients) to the service collection.
    /// Currently a stub — real implementations will be added in later phases.
    /// </summary>
    public static IServiceCollection AddFleetInfrastructure(
        this IServiceCollection services,
        FleetOptions options)
    {
        // TODO (Phase 2): Register DbContext with EF Core SQLite
        // services.AddDbContext<FleetDbContext>(opt =>
        //     opt.UseSqlite($"Data Source={options.DatabasePath}"));

        // TODO (Phase 2): Register repository implementations

        // HarnessRegistry is Singleton — any IHarness registrations MUST also be
        // Singleton to avoid a captive-dependency runtime failure.
        services.AddSingleton<IHarnessRegistry, HarnessRegistry>();

        return services;
    }
}
