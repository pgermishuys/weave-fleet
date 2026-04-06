using WeaveFleet.Application.Services;

namespace WeaveFleet.Api.Endpoints;

/// <summary>
/// Proxy endpoints that forward requests to specific running harness instances.
/// </summary>
public static class InstanceEndpoints
{
    public static WebApplication MapInstanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/instances/{id}").WithTags("Instances");

        // GET /api/instances/{id}/models — harness capabilities (model list)
        group.MapGet("/models", async (string id, InstanceTracker tracker, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            var providers = await instance.GetProvidersAsync(ct);
            // Return AvailableProvider[] — the frontend hook expects a bare array
            var result = providers.Select(p => new
            {
                id = p.Id,
                name = p.Name ?? p.Id,
                models = p.Models.Select(m => new
                {
                    id = m.Id,
                    name = m.Name ?? m.Id,
                }),
            });
            return Results.Ok(result);
        })
        .WithName("GetInstanceModels");

        // GET /api/instances/{id}/commands — available slash commands
        group.MapGet("/commands", async (string id, InstanceTracker tracker, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            var commands = await instance.GetCommandsAsync(ct);
            var result = commands.Select(c => new
            {
                name = c.Name,
                description = c.Description,
            });
            return Results.Ok(new { instanceId = id, commands = result });
        })
        .WithName("GetInstanceCommands");

        // GET /api/instances/{id}/agents — available agents
        group.MapGet("/agents", async (string id, InstanceTracker tracker, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            var agents = await instance.GetAgentsAsync(ct);
            // Return { instanceId, agents: AutocompleteAgent[] }
            var result = agents.Select(a => new
            {
                name = a.Name,
                description = a.Description,
                mode = a.Mode ?? "agent",
                hidden = a.Hidden,
                model = a.ModelProviderId is not null ? new
                {
                    providerID = a.ModelProviderId,
                    modelID = a.ModelId ?? string.Empty,
                } : null,
            });
            return Results.Ok(new { instanceId = id, agents = result });
        })
        .WithName("GetInstanceAgents");

        // GET /api/instances/{id}/find/files?q= — file search in instance working dir
        // Uses the DB-backed instance record to get the working directory, then searches filesystem.
        group.MapGet("/find/files", async (
            string id,
            string? q,
            InstanceTracker tracker,
            InstanceService instanceService,
            CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            var instanceResult = await instanceService.GetInstanceAsync(id);
            if (instanceResult.IsFailure)
                return Results.NotFound(new { error = $"Instance '{id}' not found in database." });

            var dbInstance = instanceResult.Value;
            if (string.IsNullOrWhiteSpace(q) || !Directory.Exists(dbInstance.Directory))
                return Results.Ok(new { instanceId = id, files = Array.Empty<string>() });

            var pattern = $"*{q}*";
            var files = Directory
                .EnumerateFiles(dbInstance.Directory, pattern, SearchOption.AllDirectories)
                .Take(50)
                .Select(f => f[dbInstance.Directory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToArray();

            return Results.Ok(new { instanceId = id, files });
        })
        .WithName("FindInstanceFiles");

        return app;
    }
}
