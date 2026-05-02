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
                .Select(descriptor => new
                {
                    key = new
                    {
                        providerId = descriptor.Key.ProviderId,
                        sourceType = descriptor.Key.SourceType,
                        actionId = descriptor.Key.ActionId,
                        contractVersion = descriptor.Key.ContractVersion
                    },
                    displayName = descriptor.DisplayName,
                    kind = descriptor.Kind,
                    inputFields = descriptor.InputFields.Select(field => new
                    {
                        name = field.Name,
                        valueType = field.ValueType,
                        required = field.Required,
                        allowedValues = field.AllowedValues,
                        description = field.Description
                    }),
                    producesWorkspace = descriptor.ProducesWorkspace,
                    producesContext = descriptor.ProducesContext,
                    requiresConfirmation = descriptor.RequiresConfirmation
                })
                .OrderBy(source => source.displayName, StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new { sources });
        })
        .WithName("GetSessionSourceCatalog");

        return app;
    }
}
#pragma warning restore IL2026
