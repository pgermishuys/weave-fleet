using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026

public static class AutomationEndpoints
{
    private static readonly string[] EventCatalog =
    [
        "SessionStarted", "SessionIdled", "SessionStopped", "SessionDeleted",
        "SessionArchived", "TurnStarted", "TurnEnded", "MessageCreated",
        "MessageUpdated", "DelegationCreated", "DelegationUpdated", "DelegationCompleted"
    ];

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/automations").WithTags("Automations");

        // POST / — create
        group.MapPost("/", async (CreateAutomationRequest request, AutomationService service) =>
        {
            var result = await service.CreateAsync(
                request.Name, request.Prompt, request.TriggerType, request.TriggerConfig,
                request.MaxConcurrentRuns, request.MaxRunsPerHour, request.TimeoutMinutes,
                request.WorkspaceId, request.Model, request.Agent);
            return result.Match(
                automation => Results.Created($"/api/automations/{automation.Id}", MapToResponse(automation)),
                error => error.Code switch
                {
                    var c when c.StartsWith("Validation.", StringComparison.Ordinal)
                        => Results.BadRequest(new ApiErrorResponse(error.Description)),
                    _ => Results.Problem(error.Description)
                });
        });

        // PUT /{id} — update
        group.MapPut("/{id}", async (string id, UpdateAutomationRequest request, AutomationService service) =>
        {
            var result = await service.UpdateAsync(id,
                request.Name, request.Prompt, request.TriggerType, request.TriggerConfig,
                request.MaxConcurrentRuns, request.MaxRunsPerHour, request.TimeoutMinutes,
                request.WorkspaceId, request.Model, request.Agent);
            return result.Match(
                automation => Results.Ok(MapToResponse(automation)),
                error => error.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                        => Results.NotFound(new ApiErrorResponse(error.Description)),
                    var c when c.StartsWith("Validation.", StringComparison.Ordinal)
                        => Results.BadRequest(new ApiErrorResponse(error.Description)),
                    _ => Results.Problem(error.Description)
                });
        });

        // GET / — list
        group.MapGet("/", async (string? workspaceId, AutomationService service) =>
        {
            var result = await service.ListAsync(workspaceId);
            return result.Match(
                automations => Results.Ok(new AutomationListResponse(automations.Select(MapToResponse).ToList())),
                error => Results.Problem(error.Description));
        });

        // GET /{id} — get by ID
        group.MapGet("/{id}", async (string id, AutomationService service) =>
        {
            var result = await service.GetByIdAsync(id);
            return result.Match(
                automation => Results.Ok(MapToResponse(automation)),
                error => error.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                        => Results.NotFound(new ApiErrorResponse(error.Description)),
                    _ => Results.Problem(error.Description)
                });
        });

        // DELETE /{id} — soft-delete
        group.MapDelete("/{id}", async (string id, AutomationService service) =>
        {
            var result = await service.DeleteAsync(id);
            return result.ToNoContentResult();
        });

        // POST /{id}/enable
        group.MapPost("/{id}/enable", async (string id, AutomationService service) =>
        {
            var result = await service.EnableAsync(id);
            return result.ToNoContentResult();
        });

        // POST /{id}/disable
        group.MapPost("/{id}/disable", async (string id, AutomationService service) =>
        {
            var result = await service.DisableAsync(id);
            return result.ToNoContentResult();
        });

        // POST /{id}/run — manual trigger (202 Accepted)
        group.MapPost("/{id}/run", async (string id, AutomationService service, IServiceProvider sp) =>
        {
            var result = await service.TriggerManuallyAsync(id);
            return result.Match(
                automation =>
                {
                    // Fire-and-forget execution in a new scope (scoped services die with the HTTP request)
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    _ = Task.Run(async () =>
                    {
                        using var scope = scopeFactory.CreateScope();
                        var exec = scope.ServiceProvider.GetRequiredService<AutomationExecutionService>();
                        await exec.ExecuteAsync(automation, eventType: "manual", eventSummary: "Manual trigger via API");
                    });
                    return Results.Accepted();
                },
                error => error.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
                        => Results.NotFound(new ApiErrorResponse(error.Description)),
                    _ => Results.Problem(error.Description)
                });
        });

        // GET /event-catalog
        group.MapGet("/event-catalog", () => Results.Ok(EventCatalog));

        return app;
    }

    private static AutomationResponse MapToResponse(Automation a) => new(
        a.Id, a.Name, a.Prompt, a.TriggerType, a.TriggerConfig,
        a.MaxConcurrentRuns, a.MaxRunsPerHour, a.TimeoutMinutes,
        a.IsEnabled, a.WorkspaceId, a.Model, a.Agent, a.CreatedAt, a.UpdatedAt);
}

#pragma warning restore IL2026
