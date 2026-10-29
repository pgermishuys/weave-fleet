using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Skills;
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

        group.MapPut("/{key}", async (string key, SetPreferenceRequest req, IUserPreferenceRepository repo, HttpContext http, HarnessAvailabilityCache harnessChecks) =>
        {
            // Preferences hold Fleet's safety settings (permission level) and switches (HydraFusion's credits,
            // Mods, Built-in skills, Session messages) and the program Fleet runs for GitHub Copilot, so only the user
            // sets them, never an agent through its /agent/{token} address. No key is open to agents: none of Fleet's
            // skills, plugins or tools write one.
            if (http.RefuseAgent("Fleet's settings") is { } refused)
                return refused;

            // Turning the Mods switch from off to on turns mods back on, so it ends "Start without mods".
            var mods = key == ModsFeature.PreferenceKey ? http.RequestServices.GetRequiredService<ModsFeature>() : null;
            var modsWasOn = mods is not null && await mods.IsSwitchedOnAsync();

            await repo.SetAsync(key, req.Value);

            // GitHub Copilot's settings (its path, HydraFusion) change what the harness offers: the next read checks again.
            if (key.StartsWith("copilot.", StringComparison.Ordinal))
                harnessChecks.Forget();

            if (mods is not null && !modsWasOn && await mods.IsSwitchedOnAsync())
                await http.RequestServices.GetRequiredService<ModService>().SwitchedOnAsync(http.RequestAborted);

            // fleet-mod comes with Mods, so each harness reads its built-in skills again.
            if (mods is not null && modsWasOn != await mods.IsSwitchedOnAsync())
                await http.RequestServices.GetRequiredService<BuiltInSkillService>().ModsChangedAsync();

            // The mod host follows the switch. In the background: stopping a host can take its two seconds of grace.
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
