using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Infrastructure;

namespace WeaveFleet.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddFleetInfrastructure_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var options = new FleetOptions();

        // Should not throw
        services.AddFleetInfrastructure(options);
        Assert.True(true);
    }
}
