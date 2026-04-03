namespace WeaveFleet.Api.Endpoints;

public static class DirectoryEndpoints
{
    public static WebApplication MapDirectoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api").WithTags("Directories");

        group.MapGet("/directories", () => Results.Ok(new
        {
            entries = Array.Empty<object>(),
            currentPath = "/",
            parentPath = (string?)null,
            roots = Array.Empty<object>()
        }))
        .WithName("GetDirectories");

        return app;
    }
}
