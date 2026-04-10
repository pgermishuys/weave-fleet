using WeaveFleet.Application.SessionSources;

namespace WeaveFleet.Api.Endpoints;

public static class SessionSourceEndpoints
{
    public static WebApplication MapSessionSourceEndpoints(this WebApplication app)
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
