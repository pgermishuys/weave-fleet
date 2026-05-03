using WeaveFleet.Application.Plugins;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class PluginEndpoints
{
    public static IEndpointRouteBuilder MapPluginEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Plugins");

        group.MapGet("/plugins", async (IPluginCatalog pluginCatalog, CancellationToken cancellationToken) =>
        {
            var descriptors = await pluginCatalog.GetDescriptorsAsync(cancellationToken).ConfigureAwait(false);
            var statuses = await pluginCatalog.GetStatusesAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(new PluginListResponse(
                Plugins: descriptors.Select(descriptor => new PluginDescriptorItem(
                    Id: descriptor.Id,
                    DisplayName: descriptor.DisplayName,
                    TrustLevel: ToTrustLevel(descriptor.TrustLevel),
                    HasFrontend: descriptor.HasFrontend,
                    HasBackend: descriptor.HasBackend)).ToList(),
                Statuses: statuses.Select(status => new PluginStatusItem(
                    PluginId: status.PluginId,
                    Status: ToConnectionStatus(status.Status),
                    ConnectedAt: status.ConnectedAt,
                    Actions: status.Actions.Select(action => new PluginActionItem(
                        Id: action.Id,
                        Label: action.Label,
                        Href: action.Href,
                        Method: action.Method)).ToList())).ToList()));
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

public sealed record PluginListResponse(
    IReadOnlyList<PluginDescriptorItem> Plugins,
    IReadOnlyList<PluginStatusItem> Statuses);

public sealed record PluginDescriptorItem(
    string Id,
    string DisplayName,
    string TrustLevel,
    bool HasFrontend,
    bool HasBackend);

public sealed record PluginStatusItem(
    string PluginId,
    string Status,
    DateTimeOffset? ConnectedAt,
    IReadOnlyList<PluginActionItem> Actions);

public sealed record PluginActionItem(
    string Id,
    string Label,
    string? Href,
    string? Method);

#pragma warning restore IL2026
