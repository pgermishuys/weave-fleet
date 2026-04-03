namespace WeaveFleet.Api.Endpoints;

public static class SessionEndpoints
{
    public static WebApplication MapSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        group.MapGet("/", () =>
        {
            return Results.Ok(Array.Empty<object>());
        })
        .Produces<object[]>(200)
        .WithName("GetSessions");

        group.MapGet("/{id}", (string id) =>
            Results.Json(new { error = "Not found" }, statusCode: 404))
        .WithName("GetSession");

        group.MapPost("/", () =>
            Results.Json(new { error = "Not implemented" }, statusCode: 501))
        .WithName("CreateSession");

        group.MapDelete("/{id}", (string id) =>
            Results.Json(new { error = "Not implemented" }, statusCode: 501))
        .WithName("DeleteSession");

        return app;
    }
}
