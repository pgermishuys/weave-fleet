using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
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
            FleetOptions fleetOptions,
            CancellationToken ct) =>
        {
            var harnesses = await registry.GetAvailabilityAsync(ct);
            var preferenceValues = await preferences.GetAllAsync();

            var response = harnesses.Select(harness => harness with
            {
                UserEnabled = IsHarnessUserEnabled(harness.Type, preferenceValues),
                // Profiles are off with sign-in on (see HarnessProfileService.Supports).
                Capabilities = harness.Capabilities with
                {
                    SupportsProfiles = harness.Capabilities.SupportsProfiles && !fleetOptions.Auth.Enabled,
                },
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

        // GET /api/harnesses/{type}/catalog?directory=&profile= — its agents and models before a session exists
        group.MapGet("/harnesses/{type}/catalog", async (
            string type,
            string? directory,
            string? profile,
            HarnessCatalogService catalogs,
            CancellationToken ct) =>
        {
            var result = await catalogs.GetCatalogAsync(type, directory, ct, profile);
            return result.Match(
                catalog => Results.Ok(ToCatalogResponse(catalog)),
                err => err.ToSessionApiResult());
        })
        .Produces<HarnessCatalogResponse>(200)
        .WithName("GetHarnessCatalog");

        return app;
    }

    private static HarnessCatalogResponse ToCatalogResponse(HarnessCatalog? catalog)
    {
        if (catalog is null)
            return new HarnessCatalogResponse(false, [], [], null, null);

        var agents = catalog.Agents.Select(a => new InstanceAgentItem(
            a.Name,
            a.Description,
            a.Mode ?? "agent",
            a.Hidden,
            a.ModelProviderId is not null
                ? new InstanceAgentModelRef(a.ModelProviderId, a.ModelId ?? string.Empty)
                : null)).ToList();
        var providers = catalog.Providers.Select(p => new InstanceProviderItem(
            p.Id,
            p.Name ?? p.Id,
            p.Models.Select(m => new InstanceModelItem(m.Id, m.Name ?? m.Id, m.Variants)).ToList())).ToList();
        var defaultModel = catalog.DefaultModelProviderId is { Length: > 0 } providerId && catalog.DefaultModelId is { Length: > 0 } modelId
            ? new InstanceAgentModelRef(providerId, modelId)
            : null;

        return new HarnessCatalogResponse(true, agents, providers, catalog.DefaultAgent, defaultModel);
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
