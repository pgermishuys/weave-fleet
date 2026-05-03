using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class PreferencesEndpoints
{
    public static IEndpointRouteBuilder MapPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preferences").WithTags("Preferences");

        group.MapGet("/", async (IUserPreferenceRepository repo) =>
        {
            var prefs = await repo.GetAllAsync();
            return Results.Ok(prefs);
        })
        .Produces<IReadOnlyDictionary<string, string>>(StatusCodes.Status200OK)
        .WithName("GetPreferences");

        group.MapPut("/{key}", async (string key, SetPreferenceRequest req, IUserPreferenceRepository repo) =>
        {
            await repo.SetAsync(key, req.Value);
            return Results.NoContent();
        })
        .WithName("SetPreference");

        return app;
    }
}

internal sealed record SetPreferenceRequest(string Value);

#pragma warning restore IL2026
