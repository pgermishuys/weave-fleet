using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Api.Endpoints;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class PluginEndpointsTests
{
    [Fact]
    public async Task GetPlugins_WhenCatalogHasGitHub_ReturnsDescriptorsAndStatuses()
    {
        var catalog = new StubPluginCatalog();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IPluginCatalog>(catalog);
        var app = builder.Build();
        app.MapPluginEndpoints();

        var endpointSources = ((IEndpointRouteBuilder)app).DataSources;

        var endpoint = endpointSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "/api/plugins");

        endpoint.ShouldNotBeNull();

        var descriptors = await catalog.GetDescriptorsAsync(CancellationToken.None);
        var statuses = await catalog.GetStatusesAsync(CancellationToken.None);

        descriptors.ShouldHaveSingleItem();
        descriptors[0].Id.ShouldBe("github");
        statuses.ShouldHaveSingleItem();
        statuses[0].PluginId.ShouldBe("github");
        statuses[0].Actions.Select(action => action.Id).ShouldContain("connect");
    }

    private sealed class StubPluginCatalog : IPluginCatalog
    {
        public Task<IReadOnlyList<FleetPluginDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<FleetPluginDescriptor> descriptors =
            [
                new FleetPluginDescriptor("github", "GitHub", PluginTrustLevel.BuiltIn, true, true),
            ];

            return Task.FromResult(descriptors);
        }

        public Task<IReadOnlyList<PluginStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PluginStatus> statuses =
            [
                new PluginStatus(
                    "github",
                    PluginConnectionStatus.Disconnected,
                    null,
                    [new PluginActionDescriptor("connect", "Connect", "/api/integrations/github/auth/token", "POST")]),
            ];

            return Task.FromResult(statuses);
        }
    }
}
