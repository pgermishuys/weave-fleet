using WeaveFleet.Application.Plugins;

namespace WeaveFleet.Api.Endpoints;

public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Plugins");

        group.MapGet("/plugins", async (IPluginCatalog pluginCatalog, CancellationToken cancellationToken) =>
        {
            var descriptors = await pluginCatalog.GetDescriptorsAsync(cancellationToken).ConfigureAwait(false);
            var statuses = await pluginCatalog.GetStatusesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                plugins = descriptors.Select(descriptor => new
                {
                    id = descriptor.Id,
                    displayName = descriptor.DisplayName,
                    trustLevel = ToTrustLevel(descriptor.TrustLevel),
                    hasFrontend = descriptor.HasFrontend,
                    hasBackend = descriptor.HasBackend,
                }),
                statuses = statuses.Select(status => new
                {
                    pluginId = status.PluginId,
                    status = ToConnectionStatus(status.Status),
                    connectedAt = status.ConnectedAt,
                    actions = status.Actions.Select(action => new
                    {
                        id = action.Id,
                        label = action.Label,
                        href = action.Href,
                        method = action.Method,
                    }),
                }),
            });
        })
        .WithName("GetPlugins");

        return app;
    }

    private static string ToTrustLevel(PluginTrustLevel trustLevel)
        => trustLevel switch
        {
            PluginTrustLevel.BuiltIn => "built-in",
            _ => throw new ArgumentOutOfRangeException(nameof(trustLevel), trustLevel, null),
        };

    private static string ToConnectionStatus(PluginConnectionStatus status)
        => status switch
        {
            PluginConnectionStatus.Connected => "connected",
            PluginConnectionStatus.Disconnected => "disconnected",
            PluginConnectionStatus.Error => "error",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
}
