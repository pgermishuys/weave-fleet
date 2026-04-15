using Microsoft.Extensions.DependencyInjection;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// Helper that builds a real <see cref="IServiceScopeFactory"/> from a <see cref="ServiceCollection"/>.
/// Replaces manual IServiceProvider → IServiceScope → IServiceScopeFactory mock chains.
/// </summary>
public static class TestServiceScopeFactory
{
    /// <summary>
    /// Create a real <see cref="IServiceScopeFactory"/> with the given service registrations.
    /// </summary>
    public static IServiceScopeFactory Create(Action<ServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>
    /// Create a real <see cref="IServiceScopeFactory"/> with no service registrations.
    /// Useful for tests that construct a harness runtime but never resolve from the scope.
    /// </summary>
    public static IServiceScopeFactory CreateEmpty()
        => Create(_ => { });
}
