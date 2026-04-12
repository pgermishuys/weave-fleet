using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

public sealed class GitHubBackendPlugin(
    GitHubService gitHubService,
    IPluginStateStore pluginStateStore,
    IServiceProvider serviceProvider) : IBackendPlugin
{
    public FleetPluginDescriptor Descriptor { get; } = new(
        "github",
        "GitHub",
        PluginTrustLevel.BuiltIn,
        HasFrontend: true,
        HasBackend: true);

    public async Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // IUserContext is scoped; resolve from current scope via IServiceProvider.
        // GetStatusAsync is always called within an HTTP request scope.
        var userContext = serviceProvider.GetRequiredService<IUserContext>();
        var userId = userContext.UserId;

        var connected = await gitHubService.IsConnectedAsync(userId, cancellationToken).ConfigureAwait(false);
        var state = await pluginStateStore.GetStateAsync(Descriptor.Id, userId, cancellationToken).ConfigureAwait(false);
        var connectedAt = state? ["connected_at"]?.GetValue<DateTimeOffset?>();
        var actions = connected
            ? new[]
            {
                new PluginActionDescriptor(
                    "disconnect",
                    "Disconnect",
                    "/api/integrations/github/auth",
                    "DELETE"),
            }
            : new[]
            {
                new PluginActionDescriptor(
                    "connect",
                    "Connect",
                    "/api/integrations/github/auth/token",
                    "POST"),
            };

        return new PluginStatus(
            Descriptor.Id,
            connected ? PluginConnectionStatus.Connected : PluginConnectionStatus.Disconnected,
            connectedAt,
            actions);
    }

    public void MapEndpoints(IEndpointRouteBuilder builder)
    {
        GitHubEndpointMappings.MapAuthEndpoints(builder);
        GitHubEndpointMappings.MapDataEndpoints(builder);
    }
}
