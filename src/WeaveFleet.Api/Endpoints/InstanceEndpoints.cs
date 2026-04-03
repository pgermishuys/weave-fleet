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
        // Models are harness-type-level metadata; we return an empty list for now
        // since IHarnessInstance doesn't expose per-instance model lists.
        group.MapGet("/models", (string id, InstanceTracker tracker) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            return Results.Ok(new { instanceId = id, models = Array.Empty<object>() });
        })
        .WithName("GetInstanceModels");

        // GET /api/instances/{id}/commands — available slash commands
        group.MapGet("/commands", (string id, InstanceTracker tracker) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            return Results.Ok(new { instanceId = id, commands = Array.Empty<object>() });
        })
        .WithName("GetInstanceCommands");

        // GET /api/instances/{id}/agents — available agents
        group.MapGet("/agents", (string id, InstanceTracker tracker) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new { error = $"Instance '{id}' not found or not running." });

            return Results.Ok(new { instanceId = id, agents = Array.Empty<object>() });
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
