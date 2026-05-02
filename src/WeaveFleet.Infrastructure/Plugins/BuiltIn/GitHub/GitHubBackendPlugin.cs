using System.Diagnostics.CodeAnalysis;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

public sealed class GitHubBackendPlugin(
    IHttpContextAccessor httpContextAccessor) : IBackendPlugin
{
    public FleetPluginDescriptor Descriptor { get; } = new(
        "github",
        "GitHub",
        PluginTrustLevel.BuiltIn,
        HasFrontend: true,
        HasBackend: true);

    public async Task<PluginStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var requestServices = httpContextAccessor.HttpContext?.RequestServices
            ?? throw new InvalidOperationException("GitHub plugin status requires an active HTTP request scope.");
        var userContext = requestServices.GetRequiredService<IUserContext>();
        var gitHubService = requestServices.GetRequiredService<GitHubService>();

        var connection = await gitHubService.GetConnectionStatusAsync(userContext.UserId, cancellationToken).ConfigureAwait(false);
        var actions = connection.Connected
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
            connection.Connected ? PluginConnectionStatus.Connected : PluginConnectionStatus.Disconnected,
            connection.ConnectedAt,
            actions);
    }

    [RequiresUnreferencedCode("Plugin endpoint delegates are registered at runtime via reflection-based ASP.NET Core route building; all parameter types are concrete and preserved.")]
    public void MapEndpoints(IEndpointRouteBuilder builder)
    {
        GitHubEndpointMappings.MapAuthEndpoints(builder);
        GitHubEndpointMappings.MapDataEndpoints(builder);
    }
}
