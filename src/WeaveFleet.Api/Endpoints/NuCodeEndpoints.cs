using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class NuCodeEndpoints
{
    public static IEndpointRouteBuilder MapNuCodeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/nucode").WithTags("NuCode");

        group.MapPost("/test-connection", async (
            INuCodeConnectionTester tester,
            CancellationToken ct) =>
        {
            var result = await tester.TestAsync(ct).ConfigureAwait(false);
            return Results.Ok(new NuCodeTestConnectionResponse(result.Success, result.Error, result.LatencyMs));
        })
        .Produces<NuCodeTestConnectionResponse>(StatusCodes.Status200OK)
        .WithName("TestNuCodeConnection");

        return app;
    }
}

internal sealed record NuCodeTestConnectionResponse(bool Success, string? Error, int LatencyMs);

#pragma warning restore IL2026
