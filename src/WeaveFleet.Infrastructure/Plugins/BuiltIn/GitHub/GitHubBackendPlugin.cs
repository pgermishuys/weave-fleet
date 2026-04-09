using WeaveFleet.Application.Plugins;
using Microsoft.AspNetCore.Builder;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

public sealed class GitHubBackendPlugin(GitHubService gitHubService, IPluginStateStore pluginStateStore) : IBackendPlugin
{
    public FleetPluginDescriptor Descriptor { get; } = new(
        "github",
        "GitHub",
        PluginTrustLevel.BuiltIn,
        HasFrontend: true,
        HasBackend: true);

    public async Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var connected = await gitHubService.IsConnectedAsync(cancellationToken).ConfigureAwait(false);
        var state = await pluginStateStore.GetStateAsync(Descriptor.Id, cancellationToken).ConfigureAwait(false);
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

    public void MapEndpoints(WebApplication app)
    {
        GitHubEndpointMappings.MapAuthEndpoints(app);
        GitHubEndpointMappings.MapDataEndpoints(app);
    }
}
