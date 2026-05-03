using WeaveFleet.Api;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

/// <summary>
/// Proxy endpoints that forward requests to specific running harness instances.
/// </summary>
public static class InstanceEndpoints
{
    public static IEndpointRouteBuilder MapInstanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instances/{id}").WithTags("Instances");

        // GET /api/instances/{id}/models — harness capabilities (model list)
        group.MapGet("/models", async (string id, InstanceTracker tracker, InstanceService instanceService, IUserContext userContext, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            if (!await IsOwnerAsync(instanceService, userContext, id))
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            var providers = await instance.GetProvidersAsync(ct);
            var result = providers.Select(p => new InstanceProviderItem(
                p.Id,
                p.Name ?? p.Id,
                p.Models.Select(m => new InstanceModelItem(m.Id, m.Name ?? m.Id)).ToList())).ToList();
            return Results.Ok(result);
        })
        .WithName("GetInstanceModels");

        // GET /api/instances/{id}/commands — available slash commands
        group.MapGet("/commands", async (string id, InstanceTracker tracker, InstanceService instanceService, IUserContext userContext, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            if (!await IsOwnerAsync(instanceService, userContext, id))
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            var commands = await instance.GetCommandsAsync(ct);
            var result = commands.Select(c => new InstanceCommandItem(c.Name, c.Description)).ToList();
            return Results.Ok(new InstanceCommandsResponse(id, result));
        })
        .WithName("GetInstanceCommands");

        // POST /api/instances/{id}/command — execute a slash command on the instance
        group.MapPost("/command", async (string id, SendCommandApiRequest req, InstanceTracker tracker, InstanceService instanceService, IUserContext userContext, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            if (!await IsOwnerAsync(instanceService, userContext, id))
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            var providers = await instance.GetProvidersAsync(ct);
            if (!ModelRef.TryResolve(req.Model, providers, out var resolvedModel, out var modelError))
                return Results.BadRequest(new ErrorResponse(modelError!));

            var options = new CommandOptions
            {
                Command = req.Command,
                Arguments = req.Arguments,
                Agent = req.Agent,
                ProviderId = resolvedModel.ProviderId,
                ModelId = resolvedModel.ModelId,
            };

            var validationError = options.Validate();
            if (validationError is not null)
                return Results.BadRequest(new ErrorResponse(validationError));

            await instance.SendCommandAsync(options, ct);
            return Results.Accepted();
        })
        .WithName("SendInstanceCommand");

        // GET /api/instances/{id}/agents — available agents
        group.MapGet("/agents", async (string id, InstanceTracker tracker, InstanceService instanceService, IUserContext userContext, CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            if (!await IsOwnerAsync(instanceService, userContext, id))
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            var agents = await instance.GetAgentsAsync(ct);
            var result = agents.Select(a => new InstanceAgentItem(
                a.Name,
                a.Description,
                a.Mode ?? "agent",
                a.Hidden,
                a.ModelProviderId is not null
                    ? new InstanceAgentModelRef(a.ModelProviderId, a.ModelId ?? string.Empty)
                    : null)).ToList();
            return Results.Ok(new InstanceAgentsResponse(id, result));
        })
        .WithName("GetInstanceAgents");

        // GET /api/instances/{id}/find/files?q= — file search in instance working dir
        // Uses the DB-backed instance record to get the working directory, then searches filesystem.
        group.MapGet("/find/files", async (
            string id,
            string? q,
            InstanceTracker tracker,
            InstanceService instanceService,
            IUserContext userContext,
            CancellationToken ct) =>
        {
            var instance = tracker.Get(id);
            if (instance is null)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            if (!await IsOwnerAsync(instanceService, userContext, id))
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found or not running."));

            var instanceResult = await instanceService.GetInstanceAsync(id);
            if (instanceResult.IsFailure)
                return Results.NotFound(new ErrorResponse($"Instance '{id}' not found in database."));

            var dbInstance = instanceResult.Value;
            if (string.IsNullOrWhiteSpace(q) || !Directory.Exists(dbInstance.Directory))
                return Results.Ok(new InstanceFilesResponse(id, Array.Empty<string>()));

            var pattern = $"*{q}*";
            var files = Directory
                .EnumerateFiles(dbInstance.Directory, pattern, SearchOption.AllDirectories)
                .Take(50)
                .Select(f => f[dbInstance.Directory.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .ToArray();

            return Results.Ok(new InstanceFilesResponse(id, files));
        })
        .WithName("FindInstanceFiles");

        return app;
    }

    /// <summary>
    /// Verifies the requesting user owns the instance by checking the DB record's UserId.
    /// Returns false if the instance doesn't exist in the DB or the user doesn't match.
    /// </summary>
    private static async Task<bool> IsOwnerAsync(InstanceService instanceService, IUserContext userContext, string instanceId)
    {
        var result = await instanceService.GetInstanceAsync(instanceId);
        if (result.IsFailure)
            return false;

        return string.Equals(result.Value.UserId, userContext.UserId, StringComparison.Ordinal);
    }
}
#pragma warning restore IL2026
