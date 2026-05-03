using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class HarnessEndpoints
{
    public static IEndpointRouteBuilder MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Harnesses");

        group.MapGet("/harnesses", async (
            IHarnessRegistry registry,
            CancellationToken ct) =>
        {
            var harnesses = await registry.GetAvailabilityAsync(ct);
            return Results.Ok(harnesses);
        })
        .WithName("GetHarnesses");

        return app;
    }
}
#pragma warning restore IL2026
