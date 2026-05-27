using System.Security.Claims;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Harnesses.OpenCode.Pooling;

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

        group.MapGet("/opencode/pool", (
            HttpContext httpContext,
            FleetOptions fleetOptions,
            IOpenCodePoolHealthCheck poolHealthCheck) =>
        {
            if (!IsAdmin(httpContext, fleetOptions))
            {
                return Results.Forbid();
            }

            return Results.Ok(poolHealthCheck.GetStatus());
        })
        .Produces<OpenCodePoolHealthStatus>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status403Forbidden)
        .WithName("GetOpenCodePoolHealth");

        return app;
    }

    private static bool IsAdmin(HttpContext httpContext, FleetOptions fleetOptions)
    {
        if (!fleetOptions.Auth.Enabled)
        {
            return true;
        }

        var user = httpContext.User;
        return user.IsInRole("admin")
            || HasClaimValue(user, "fleet_admin", "true")
            || HasClaimValue(user, "role", "admin")
            || HasClaimValue(user, "roles", "admin");
    }

    private static bool HasClaimValue(ClaimsPrincipal user, string claimType, string expectedValue)
        => user.Claims.Any(claim =>
            string.Equals(claim.Type, claimType, StringComparison.OrdinalIgnoreCase)
            && claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(value => string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase)));

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
