using WeaveFleet.Application.Harnesses;
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
