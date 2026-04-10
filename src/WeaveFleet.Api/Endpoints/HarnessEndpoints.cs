using WeaveFleet.Application.Harnesses;

namespace WeaveFleet.Api.Endpoints;

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
