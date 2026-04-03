namespace WeaveFleet.Api.Endpoints;

public static class HarnessEndpoints
{
    public static WebApplication MapHarnessEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Harnesses");

        group.MapGet("/harnesses", () => Results.Ok(Array.Empty<object>()))
        .WithName("GetHarnesses");

        return app;
    }
}
