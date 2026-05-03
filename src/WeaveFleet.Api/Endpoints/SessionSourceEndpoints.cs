using WeaveFleet.Application.SessionSources;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class SessionSourceEndpoints
{
    public static IEndpointRouteBuilder MapSessionSourceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/session-sources").WithTags("SessionSources");

        group.MapGet("/catalog", (IEnumerable<ISessionSourceProvider> providers) =>
        {
            var sources = providers
                .SelectMany(provider => provider.GetDescriptors())
                .Select(descriptor => new SessionSourceItem(
                    Key: new SessionSourceKey(
                        descriptor.Key.ProviderId,
                        descriptor.Key.SourceType,
                        descriptor.Key.ActionId,
                        descriptor.Key.ContractVersion),
                    DisplayName: descriptor.DisplayName,
                    Kind: descriptor.Kind,
                    InputFields: descriptor.InputFields.Select(field => new SessionSourceInputField(
                        field.Name,
                        field.ValueType,
                        field.Required,
                        field.AllowedValues,
                        field.Description)).ToList(),
                    ProducesWorkspace: descriptor.ProducesWorkspace,
                    ProducesContext: descriptor.ProducesContext,
                    RequiresConfirmation: descriptor.RequiresConfirmation))
                .OrderBy(source => source.DisplayName, StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new SessionSourceCatalogResponse(sources));
        })
        .WithName("GetSessionSourceCatalog");

        return app;
    }
}
#pragma warning restore IL2026
