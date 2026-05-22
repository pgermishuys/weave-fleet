using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin");

        group.MapPost("/import-legacy-sessions", async (
            ILegacySessionImporter importer,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await importer.ImportAsync(cancellationToken);
                return Results.Ok(ToResponse(result, []));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new LegacySessionImportApiResponse(
                    Imported: false,
                    Skipped: false,
                    SourcePath: string.Empty,
                    Count: 0,
                    Status: "failed",
                    Errors: [ex.Message]));
            }
        })
        .Produces<LegacySessionImportApiResponse>(StatusCodes.Status200OK)
        .Produces<LegacySessionImportApiResponse>(StatusCodes.Status409Conflict)
        .WithName("ImportLegacySessions");

        return app;
    }

    private static LegacySessionImportApiResponse ToResponse(
        LegacySessionImportResult result,
        IReadOnlyList<string> errors)
        => new(
            result.Imported,
            result.Skipped,
            result.SourcePath,
            result.SessionCount,
            result.Status,
            errors);
}

public sealed record LegacySessionImportApiResponse(
    bool Imported,
    bool Skipped,
    string SourcePath,
    int Count,
    string Status,
    IReadOnlyList<string> Errors);
