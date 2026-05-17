using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NuCode.Events;

namespace NuCode;

public sealed class NuCodeServiceCollectionExtensionsTests
{
    [Fact]
    public void AddNuCodeRegistersOptions()
    {
        var services = new ServiceCollection();

        services.AddNuCode();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NuCodeOptions>>();

        options.Value.ShouldNotBeNull();
    }

    [Fact]
    public void AddNuCodeAppliesConfiguration()
    {
        var services = new ServiceCollection();

        services.AddNuCode(options =>
        {
            options.WorkingDirectory = "/custom/path";
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NuCodeOptions>>();

        options.Value.WorkingDirectory.ShouldBe("/custom/path");
    }

    [Fact]
    public void AddNuCodeReturnsServiceCollectionForChaining()
    {
        var services = new ServiceCollection();

        var result = services.AddNuCode();

        result.ShouldBeSameAs(services);
    }

    [Fact]
    public void AddNuCodeRegistersEventBusAsScoped()
    {
        var services = new ServiceCollection();
        services.AddNuCode();
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<INuCodeEventBus>();

        bus.ShouldNotBeNull();
        bus.ShouldBeOfType<NuCodeEventBus>();
    }

    [Fact]
    public void AddNuCodeRegistersScopedEventBusDifferentPerScope()
    {
        var services = new ServiceCollection();
        services.AddNuCode();
        var provider = services.BuildServiceProvider();

        INuCodeEventBus bus1;
        INuCodeEventBus bus2;

        using (var scope1 = provider.CreateScope())
        {
            bus1 = scope1.ServiceProvider.GetRequiredService<INuCodeEventBus>();
        }

        using (var scope2 = provider.CreateScope())
        {
            bus2 = scope2.ServiceProvider.GetRequiredService<INuCodeEventBus>();
        }

        bus2.ShouldNotBeSameAs(bus1);
    }

    [Fact]
    public void AddNuCodeRegistersGlobalEventBusAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddNuCode();
        var provider = services.BuildServiceProvider();

        var global1 = provider.GetRequiredService<GlobalEventBus>();
        var global2 = provider.GetRequiredService<GlobalEventBus>();

        global1.ShouldNotBeNull();
        global2.ShouldBeSameAs(global1);
    }
}
