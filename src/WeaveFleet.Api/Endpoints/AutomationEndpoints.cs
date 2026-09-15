using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Api.Contracts;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026

public static class AutomationEndpoints
{
    /// <summary>
    /// The event types an automation can wait for: the outbox message types that reach
    /// <c>AutomationEventDispatcherService</c>. Offer nothing else here, because a trigger only fires when its
    /// event type equals one of these exactly.
    /// </summary>
    private static readonly string[] EventCatalog =
    [
        "session_created", "session_archived", "session_deleted",
        "delegation.created", "delegation.updated"
    ];

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/automations").WithTags("Automations");

        // POST / — create
        group.MapPost("/", async (CreateAutomationRequest request, AutomationService service, TimeProvider time) =>
        {
            var result = await service.CreateAsync(
                request.Name, request.Prompt, request.TriggerType, request.TriggerConfig,
                request.MaxConcurrentRuns, request.MaxRunsPerHour, request.TimeoutMinutes,
                request.WorkspaceId, request.Model, request.Agent, request.TargetTags, request.TargetType,
                request.TimeZone, request.Isolation, request.BaseBranch);
            return result.IsSuccess
                ? Results.Created($"/api/automations/{result.Value.Id}", MapToResponse(result.Value, null, time))
                : ErrorResult(result.Error);
        });

        // PUT /{id} — update
        group.MapPut("/{id}", async (
            string id,
            UpdateAutomationRequest request,
            AutomationService service,
            IAutomationRunRepository runs,
            AutomationRunService runService,
            TimeProvider time) =>
        {
            var result = await service.UpdateAsync(id,
                request.Name, request.Prompt, request.TriggerType, request.TriggerConfig,
                request.MaxConcurrentRuns, request.MaxRunsPerHour, request.TimeoutMinutes,
                request.WorkspaceId, request.Model, request.Agent, request.TargetTags, request.TargetType,
                request.TimeZone, request.Isolation, request.BaseBranch);
            if (result.IsFailure)
                return ErrorResult(result.Error);

            return Results.Ok(MapToResponse(result.Value, MapRun(await LatestRunAsync(runs, id), runService), time));
        });

        // GET / — list, each with when it runs next and its latest run
        group.MapGet("/", async (
            string? workspaceId,
            AutomationService service,
            IAutomationRunRepository runs,
            AutomationRunService runService,
            TimeProvider time) =>
        {
            var result = await service.ListAsync(workspaceId);
            if (result.IsFailure)
                return Results.Problem(result.Error.Description);

            var latest = await runs.GetLatestPerAutomationAsync();
            return Results.Ok(new AutomationListResponse(result.Value
                .Select(automation => MapToResponse(automation, MapRun(latest.GetValueOrDefault(automation.Id), runService), time))
                .ToList()));
        });

        // GET /{id} — get by ID
        group.MapGet("/{id}", async (
            string id,
            AutomationService service,
            IAutomationRunRepository runs,
            AutomationRunService runService,
            TimeProvider time) =>
        {
            var result = await service.GetByIdAsync(id);
            if (result.IsFailure)
                return ErrorResult(result.Error);

            return Results.Ok(MapToResponse(result.Value, MapRun(await LatestRunAsync(runs, id), runService), time));
        });

        // GET /{id}/runs — newest first
        group.MapGet("/{id}/runs", async (
            string id,
            int? limit,
            AutomationService service,
            IAutomationRunRepository runs,
            AutomationRunService runService) =>
        {
            // Loading it as its owner first is what keeps someone else's runs out.
            var result = await service.GetByIdAsync(id);
            if (result.IsFailure)
                return ErrorResult(result.Error);

            var list = await runs.ListByAutomationAsync(id, Math.Clamp(limit ?? 50, 1, 200));
            return Results.Ok(new AutomationRunListResponse(list.Select(run => MapRun(run, runService)!).ToList()));
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
            return result.IsSuccess ? Results.NoContent() : ErrorResult(result.Error);
        });

        // POST /{id}/disable
        group.MapPost("/{id}/disable", async (string id, AutomationService service) =>
        {
            var result = await service.DisableAsync(id);
            return result.ToNoContentResult();
        });

        // POST /{id}/run — Run now: records the run, answers with it (202), and starts its session in the background
        group.MapPost("/{id}/run", async (string id, AutomationService service, AutomationRunService runService, IServiceProvider sp) =>
        {
            var result = await service.TriggerManuallyAsync(id);
            if (result.IsFailure)
                return ErrorResult(result.Error);

            var automation = result.Value;
            var trigger = new AutomationRunTrigger(AutomationRunTrigger.Manual);
            var run = await runService.BeginAsync(automation, trigger);

            // A new scope, because scoped services die with the HTTP request.
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                var scopedRuns = scope.ServiceProvider.GetRequiredService<AutomationRunService>();
                await scopedRuns.FinishAsync(automation, run, trigger);
            });
            return Results.Accepted($"/api/automations/{automation.Id}/runs", MapRun(run, runService));
        });

        // GET /draft-from-session/{sessionId} — "Repeat on a schedule…": a session's first message and its folder
        group.MapGet("/draft-from-session/{sessionId}", async (string sessionId, AutomationDraftService drafts) =>
        {
            var result = await drafts.FromSessionAsync(sessionId);
            return result.IsSuccess
                ? Results.Ok(new AutomationDraftResponse(result.Value.Prompt, result.Value.Folder, result.Value.Isolation))
                : ErrorResult(result.Error);
        });

        // GET /event-catalog
        group.MapGet("/event-catalog", () => Results.Ok(EventCatalog));

        return app;
    }

    private static async Task<AutomationRun?> LatestRunAsync(IAutomationRunRepository runs, string automationId)
    {
        var latest = await runs.ListByAutomationAsync(automationId, limit: 1);
        return latest.Count > 0 ? latest[0] : null;
    }

    private static IResult ErrorResult(FleetError error) => error.Code switch
    {
        var c when c.EndsWith(".NotFound", StringComparison.Ordinal) || c == "General.NotFound"
            => Results.NotFound(new ApiErrorResponse(error.Description)),
        var c when c.StartsWith("Validation.", StringComparison.Ordinal)
            => Results.BadRequest(new ApiErrorResponse(error.Description)),
        _ => Results.Problem(error.Description)
    };

    private static AutomationRunResponse? MapRun(AutomationRun? run, AutomationRunService runService) => run is null
        ? null
        : new AutomationRunResponse(
            run.Id, run.AutomationId, run.Trigger, run.ScheduledFor, run.StartedAt,
            runService.StateOf(run), run.SessionId, run.InstanceId, run.Error);

    private static AutomationResponse MapToResponse(Automation a, AutomationRunResponse? lastRun, TimeProvider time) => new(
        a.Id, a.Name, a.Prompt, a.TriggerType, a.TriggerConfig,
        a.MaxConcurrentRuns, a.MaxRunsPerHour, a.TimeoutMinutes,
        a.IsEnabled, a.WorkspaceId, a.Model, a.Agent, a.CreatedAt, a.UpdatedAt, a.TargetTags, a.TargetType, a.TimeZone,
        a.Isolation, a.BaseBranch,
        a.IsEnabled ? AutomationSchedule.NextOccurrenceUtc(a, time.GetUtcNow().UtcDateTime)?.ToString("O") : null,
        lastRun);
}

#pragma warning restore IL2026
