using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Api.Endpoints;

namespace WeaveFleet.Api.Tests.Endpoints;

public sealed class GitHubPluginRegistrationTests
{
    [Fact]
    public void MapBackendPluginEndpoints_MapsGitHubCompatibilityRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IBackendPlugin, StubBackendPlugin>();
        var app = builder.Build();

        app.MapBackendPluginEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();

        routes.ShouldContain("/api/integrations/github/auth/status");
        routes.ShouldContain("/api/integrations/github/repos");
    }

    private sealed class StubBackendPlugin : IBackendPlugin
    {
        public FleetPluginDescriptor Descriptor =>
            new("github", "GitHub", PluginTrustLevel.BuiltIn, true, true);

        public Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PluginStatus("github", PluginConnectionStatus.Disconnected, null, []));
        }

        public void MapEndpoints(IEndpointRouteBuilder builder)
        {
            builder.MapGet("/api/integrations/github/auth/status", () => Results.Ok());
            builder.MapGet("/api/integrations/github/repos", () => Results.Ok());
        }
    }
}
