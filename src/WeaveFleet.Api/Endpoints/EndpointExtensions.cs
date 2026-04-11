using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Extension methods for registering all Fleet API endpoints.
/// </summary>
public static class EndpointExtensions
{
    public static WebApplication MapFleetEndpoints(this WebApplication app)
    {
        var fleetOptions = app.Services.GetRequiredService<FleetOptions>();

        // When auth is enabled, wrap all API + WebSocket endpoints under a RequireAuthorization group.
        // Health checks (/healthz, /readyz) are registered separately in Program.cs and remain public.
        IEndpointRouteBuilder apiScope = app;

        if (fleetOptions.Auth.Enabled)
        {
            var authenticatedGroup = app.MapGroup("/")
                .RequireAuthorization("FleetUser")
                .AddEndpointFilter(async (context, next) =>
                {
                    var userContext = context.HttpContext.RequestServices.GetRequiredService<IUserContext>();
                    if (userContext.IsAuthenticated)
                    {
                        var userService = context.HttpContext.RequestServices.GetRequiredService<UserService>();
                        await userService.EnsureUserAsync(userContext);
                    }

                    return await next(context);
                });

            apiScope = authenticatedGroup;
        }

        apiScope.MapPluginEndpoints();
        apiScope.MapSessionEndpoints();
        apiScope.MapProjectEndpoints();
        apiScope.MapFleetSummaryEndpoints();
        apiScope.MapSessionSourceEndpoints();
        apiScope.MapConfigEndpoints();
        apiScope.MapDirectoryEndpoints();
        apiScope.MapOpenDirectoryEndpoints();
        apiScope.MapSkillEndpoints();
        apiScope.MapInstanceEndpoints();
        apiScope.MapHarnessEndpoints();
        apiScope.MapWorkspaceRootEndpoints();
        apiScope.MapWorkspaceEndpoints();
        apiScope.MapSessionEventEndpoints();
        apiScope.MapWebSocketEndpoints();
        apiScope.MapAnalyticsEndpoints();
        apiScope.MapUserEndpoints();
        apiScope.MapCredentialEndpoints();
        apiScope.MapClientConfigEndpoints(fleetOptions);
        app.MapBackendPluginEndpoints();

        return app;
    }

    public static WebApplication MapBackendPluginEndpoints(this WebApplication app)
    {
        foreach (var plugin in app.Services.GetServices<IBackendPlugin>())
        {
            plugin.MapEndpoints(app);
        }

        return app;
    }

    /// <summary>
    /// Creates a route group and optionally requires authorization for all routes when auth is enabled.
    /// </summary>
    public static RouteGroupBuilder MapAuthenticatedGroup(
        this IEndpointRouteBuilder builder,
        string prefix,
        FleetOptions options)
    {
        var group = builder.MapGroup(prefix);

        if (options.Auth.Enabled)
            group.RequireAuthorization("FleetUser");

        return group;
    }
}
