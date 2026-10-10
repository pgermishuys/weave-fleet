using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Users;
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

        group.MapPut("/{key}", async (string key, SetPreferenceRequest req, IUserPreferenceRepository repo, HttpContext http) =>
        {
            // Turning the Mods switch from off to on turns mods back on, so it ends "Start without mods".
            var mods = key == ModsFeature.PreferenceKey ? http.RequestServices.GetRequiredService<ModsFeature>() : null;
            var modsWasOn = mods is not null && await mods.IsSwitchedOnAsync();

            await repo.SetAsync(key, req.Value);

            if (mods is not null && !modsWasOn && await mods.IsSwitchedOnAsync())
                await http.RequestServices.GetRequiredService<ModService>().SwitchedOnAsync(http.RequestAborted);

            // The mod host follows the switch: started when mods should run, stopped when they shouldn't. In the
            // background, since stopping a host can take its two seconds of grace.
            if (mods is not null)
            {
                var host = http.RequestServices.GetRequiredService<IModHost>();
                var userId = http.RequestServices.GetRequiredService<IUserContext>().UserId;
                _ = Task.Run(() => host.EnsureAsync(userId, CancellationToken.None));
            }

            return Results.NoContent();
        })
        .WithName("SetPreference");

        return app;
    }
}

internal sealed record SetPreferenceRequest(string Value);

#pragma warning restore IL2026
