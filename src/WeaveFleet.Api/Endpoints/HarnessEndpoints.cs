using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class HarnessEndpoints
{
    public static IEndpointRouteBuilder MapHarnessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Harnesses");

        group.MapGet("/harnesses", async (
            IHarnessRegistry registry,
            IUserPreferenceRepository preferences,
            CancellationToken ct) =>
        {
            var harnesses = await registry.GetAvailabilityAsync(ct);
            var preferenceValues = await preferences.GetAllAsync();

            var response = harnesses.Select(harness => harness with
            {
                UserEnabled = IsHarnessUserEnabled(harness.Type, preferenceValues)
            }).ToList();

            return Results.Ok(response);
        })
        .WithName("GetHarnesses");

        // POST /api/harnesses/opencode/warmup
        // Server-trust warmup: owner identity is derived exclusively from the server-authenticated
        // IUserContext / request principal. No caller-controlled owner ID, credential hash,
        // resume token, or workspace directory is accepted. In auth-enabled mode, requests with
        // no authenticated user context return 204 (no-op) instead of attempting warmup.
        group.MapPost("/harnesses/opencode/warmup", async (
            IHarnessRegistry registry,
            IUserContext userContext,
            FleetOptions fleetOptions,
            CancellationToken ct) =>
        {
            if (fleetOptions.Auth.Enabled && !userContext.IsAuthenticated)
                return Results.NoContent();

            var runtime = registry.GetRuntimeByType("opencode");
            if (runtime is null)
                return Results.NoContent();

            await runtime.WarmupPooledInstanceAsync(userContext.UserId, ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent)
        .WithName("WarmupOpenCodeHarness");

        return app;
    }

    private static bool IsHarnessUserEnabled(
        string harnessType,
        IReadOnlyDictionary<string, string> preferenceValues)
    {
        var preferenceKey = $"{harnessType}.enabled";
        if (preferenceValues.TryGetValue(preferenceKey, out var value))
        {
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(harnessType, "opencode", StringComparison.OrdinalIgnoreCase);
    }
}
#pragma warning restore IL2026
