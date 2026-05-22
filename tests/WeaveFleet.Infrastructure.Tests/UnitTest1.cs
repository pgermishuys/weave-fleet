using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void add_fleet_infrastructure_does_not_throw()
    {
        var services = new ServiceCollection();
        var options = new FleetOptions();

        // Should not throw
        services.AddFleetInfrastructure(options);
        true.ShouldBeTrue();
    }

    [Fact]
    public void add_fleet_infrastructure_registers_legacy_session_importer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUserContext, TestUserContext>();

        services.AddFleetInfrastructure(new FleetOptions { AnalyticsEnabled = false });

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var importer = scope.ServiceProvider.GetRequiredService<ILegacySessionImporter>();

        importer.ShouldBeOfType<LegacySessionImporter>();
    }

    [Fact]
    public void add_legacy_session_import_startup_service_registers_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddLegacySessionImportStartupService();

        using var serviceProvider = services.BuildServiceProvider();
        var hostedServices = serviceProvider.GetServices<IHostedService>();

        hostedServices.ShouldContain(service => service is LegacySessionImportStartupService);
    }
}
