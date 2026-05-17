using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Plugins;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Extension methods for registering all Fleet API endpoints.
/// </summary>
public static class EndpointExtensions
{
    public static WebApplication MapFleetEndpoints(this WebApplication app)
    {
        var fleetOptions = app.Services.GetRequiredService<FleetOptions>();

        // When cloud auth or local token auth is enabled, wrap all API + WebSocket endpoints
        // under a RequireAuthorization group.
        // Health checks (/healthz, /readyz) are registered separately in Program.cs and remain public.
        IEndpointRouteBuilder apiScope = app;

        if (RequiresFleetAuthorization(fleetOptions))
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
        apiScope.MapBoardEndpoints();
        apiScope.MapSessionEndpoints();
        apiScope.MapProjectEndpoints();
        apiScope.MapFleetSummaryEndpoints();
        apiScope.MapUpdateEndpoints();
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
        apiScope.MapBackendPluginEndpoints();
        apiScope.MapSmartLinkEndpoints();
        apiScope.MapTelemetryEndpoints();

        return app;
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "IBackendPlugin.MapEndpoints is called with known concrete plugin types whose parameter types are preserved at runtime.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "IBackendPlugin.MapEndpoints is called with known concrete plugin types whose parameter types are preserved at runtime.")]
    public static IEndpointRouteBuilder MapBackendPluginEndpoints(this IEndpointRouteBuilder builder)
    {
        foreach (var plugin in builder.ServiceProvider.GetServices<IBackendPlugin>())
        {
            plugin.MapEndpoints(builder);
        }

        return builder;
    }

    /// <summary>
    /// Creates a route group and optionally requires authorization for all routes when cloud auth or local token auth is enabled.
    /// </summary>
    public static RouteGroupBuilder MapAuthenticatedGroup(
        this IEndpointRouteBuilder builder,
        string prefix,
        FleetOptions options)
    {
        var group = builder.MapGroup(prefix);

        if (RequiresFleetAuthorization(options))
            group.RequireAuthorization("FleetUser");

        return group;
    }

    private static bool RequiresFleetAuthorization(FleetOptions options)
        => options.Auth.Enabled || (!options.Auth.Enabled && options.Auth.TokenAuthEnabled);
}
#pragma warning restore IL2026
