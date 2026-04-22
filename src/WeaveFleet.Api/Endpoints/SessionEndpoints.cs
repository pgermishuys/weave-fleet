using System.Text.Json.Serialization;
using System.Text.Json;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        // GET /api/sessions?limit=&offset=&status=&projectId=
        group.MapGet("/", async (
            SessionService sessionService,
            ISessionRepository sessionRepository,
            ISessionSourceUsageRepository sessionSourceUsageRepository,
            IProjectRepository projectRepository,
            int limit = 100,
            int offset = 0,
            string? status = null,
            string? retentionStatus = null,
            string? projectId = null) =>
        {
            IReadOnlyList<string>? statuses = status is not null
                ? [status]
                : null;

            var result = await sessionService.ListSessionsAsync(limit, offset, statuses, projectId, retentionStatus);
            return await result.Match<Task<IResult>>(async sessions =>
                {
                    var projectNamesById = (await projectRepository.ListAsync())
                        .ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);

                    var parentIdsWithBusyChildren = (await sessionRepository.GetIdsWithActiveChildrenAsync())
                        .ToHashSet(StringComparer.Ordinal);

                    var originsBySessionId = await sessionSourceUsageRepository.GetPrimaryBySessionIdsAsync(
                        sessions.Select(session => session.Id).ToArray());

                    return Results.Ok(sessions.Select(session => ToListResponse(session, parentIdsWithBusyChildren, projectNamesById, originsBySessionId)).ToList());
                },
                error => Task.FromResult(Results.Problem(error.Description) as IResult));
        })
        .Produces<List<SessionListResponse>>(200)
        .WithName("GetSessions");

        // GET /api/sessions/{id}
        group.MapGet("/{id}", async (
            string id,
            SessionService sessionService,
            IWorkspaceRepository workspaceRepository,
            ISessionSourceUsageRepository sessionSourceUsageRepository) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return await result.Match<Task<IResult>>(
                async session =>
                {
                    var workspace = await workspaceRepository.GetByIdAsync(session.WorkspaceId);
                    var primaryOrigin = await sessionSourceUsageRepository.GetPrimaryBySessionIdAsync(session.Id);

                    return Results.Ok(new
                    {
                        id = session.Id,
                        instanceId = session.InstanceId,
                        workspaceId = session.WorkspaceId,
                        workspaceDirectory = workspace?.Directory ?? session.Directory,
                        workspaceDisplayName = workspace?.DisplayName,
                        sourceDirectory = workspace?.SourceDirectory,
                        isolationStrategy = workspace?.IsolationStrategy ?? "existing",
                        branch = workspace?.Branch,
                        title = session.Title,
                        createdAt = session.CreatedAt,
                        stoppedAt = session.StoppedAt,
                        activityStatus = session.ActivityStatus,
                        lifecycleStatus = session.LifecycleStatus,
                        retentionStatus = session.RetentionStatus,
                        archivedAt = session.ArchivedAt,
                        totalTokens = session.TotalTokens > 0 ? session.TotalTokens : (int?)null,
                        totalCost = session.TotalCost > 0 ? session.TotalCost : (double?)null,
                        harnessType = session.HarnessType,
                        projectId = session.ProjectId,
                        origin = primaryOrigin is not null ? ToOriginDto(primaryOrigin) : null
                    });
                },
                error => Task.FromResult(error.ToSessionApiResult()));
        })
        .WithName("GetSession");

        // GET /api/sessions/{id}/origin
        group.MapGet("/{id}/origin", async (
            string id,
            SessionService sessionService,
            ISessionSourceUsageRepository sessionSourceUsageRepository) =>
        {
            var sessionResult = await sessionService.GetSessionAsync(id);
            return await sessionResult.Match<Task<IResult>>(
                async _ =>
                {
                    var usages = await sessionSourceUsageRepository.ListBySessionIdAsync(id);
                    var provenance = usages
                        .OrderBy(usage => TryParseUnixMs(usage.CreatedAt))
                        .Select(ToOriginRecordDto)
                        .ToList();

                    return Results.Ok(provenance);
                },
                error => Task.FromResult(error.ToSessionApiResult()));
        })
        .Produces<IReadOnlyList<SessionOriginRecordDto>>(200)
        .Produces(404)
        .WithName("GetSessionOrigin");

        // GET /api/sessions/{id}/delegations
        group.MapGet("/{id}/delegations", async (
            string id,
            SessionService sessionService,
            DelegationService delegationService) =>
        {
            var sessionResult = await sessionService.GetSessionAsync(id);
            return await sessionResult.Match<Task<IResult>>(
                async _ => Results.Ok(await delegationService.GetDelegationsAsync(id)),
                error => Task.FromResult(error.ToSessionApiResult()));
        })
        .Produces<IReadOnlyList<DelegationDto>>(200)
        .Produces(404)
        .WithName("GetSessionDelegations");

        // POST /api/sessions — create session via orchestrator
        group.MapPost("/", async (CreateSessionApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.CreateSessionAsync(new CreateSessionRequest
            {
                Directory = req.Directory,
                Title = req.Title,
                IsolationStrategy = req.IsolationStrategy,
                Branch = req.Branch,
                HarnessType = req.HarnessType,
                InitialPrompt = req.InitialPrompt,
                Source = req.Source,
                OnCompleteTargetSessionId = req.OnComplete?.NotifySessionId,
                OnCompleteTargetInstanceId = req.OnComplete?.NotifyInstanceId,
                ProjectId = req.ProjectId
            });
            return result.Match(
                r => Results.Ok(new
                {
                    instanceId = r.InstanceId,
                    workspaceId = r.WorkspaceId,
                    session = r.Session
                }),
                err => err.ToSessionApiResult());
        })
        .WithName("CreateSession");

        group.MapPost("/{id}/source-preview", async (string id, PreviewSessionSourceApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.PreviewAddSourceToSessionAsync(id, req.Source);
            return result.Match(
                envelope => Results.Ok(new
                {
                    preview = new
                    {
                        originLabel = envelope.OriginLabel,
                        content = envelope.Content,
                        isTruncated = envelope.IsTruncated,
                        characterCount = envelope.CharacterCount
                    }
                }),
                err => err.ToSessionApiResult());
        })
        .WithName("PreviewSessionSource");

        group.MapPost("/{id}/sources", async (string id, AddSessionSourceApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.AddSourceToSessionAsync(id, req.Source, req.Confirm);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("AddSessionSource");

        // POST /api/sessions/{id}/prompt
        group.MapPost("/{id}/prompt", async (string id, SendPromptApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var options = req.Agent is not null || req.Model is not null
                ? new PromptOptions { Agent = req.Agent, ModelId = req.Model }
                : null;
            var result = await orchestrator.PromptSessionAsync(id, req.Text, options);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("PromptSession");

        // POST /api/sessions/{id}/abort
        group.MapPost("/{id}/abort", async (string id, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.AbortSessionAsync(id);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("AbortSession");

        // POST /api/sessions/{id}/resume
        group.MapPost("/{id}/resume", async (string id, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.ResumeSessionAsync(id);
            return result.Match(
                session => Results.Ok(new
                {
                    instanceId = session.InstanceId,
                    session
                }),
                err => err.ToSessionApiResult());
        })
        .WithName("ResumeSession");

        // POST /api/sessions/{id}/stop
        group.MapPost("/{id}/stop", async (string id, SessionService sessionService) =>
        {
            var result = await sessionService.StopSessionAsync(id);
            return result.ToNoContentResult();
        })
        .WithName("StopSession");

        // POST /api/sessions/{id}/fork
        group.MapPost("/{id}/fork", async (string id, ForkSessionApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.ForkSessionAsync(id, req.Title);
            return result.Match(
                r => Results.Ok(new
                {
                    instanceId = r.InstanceId,
                    workspaceId = r.WorkspaceId,
                    session = r.Session,
                    forkedFromSessionId = id
                }),
                err => err.ToSessionApiResult());
        })
        .WithName("ForkSession");

        // GET /api/sessions/{id}/messages?limit=N&before=CURSOR
        group.MapGet("/{id}/messages", async (string id, int? limit, string? before, SessionOrchestrator orchestrator) =>
        {
            var query = (limit is not null || before is not null)
                ? new MessageQuery(limit, before)
                : null;
            var result = await orchestrator.GetSessionMessagesAsync(id, query);
            return result.Match(
                page =>
                {
                    var sanitizedMessages = ClientPayloadSanitizer.SanitizeMessages(page.Messages);
                    var oldest = page.Messages.Count > 0 ? page.Messages[0].Id : null;
                    return Results.Ok(new
                    {
                        messages = sanitizedMessages,
                        pagination = new
                        {
                            hasMore = page.HasMore,
                            oldestMessageId = oldest,
                            totalCount = sanitizedMessages.Count
                        }
                    });
                },
                err => err.ToSessionApiResult());
        })
        .WithName("GetSessionMessages");

        // GET /api/sessions/{id}/committed-events?afterSequenceNumber=N&limit=M
        group.MapGet("/{id}/committed-events", async (
            string id,
            long afterSequenceNumber,
            int? limit,
            SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.GetCommittedEventsAsync(id, afterSequenceNumber, limit);
            return result.Match(
                events => Results.Ok(new
                {
                    events = events.Select(evt => new
                    {
                        sequenceNumber = evt.SequenceNumber,
                        topic = evt.Topic,
                        type = evt.Type,
                        payload = JsonSerializer.Deserialize<JsonElement>(evt.Payload),
                        timestamp = evt.Timestamp.ToUnixTimeMilliseconds()
                    })
                }),
                err => err.ToSessionApiResult());
        })
        .WithName("GetCommittedSessionEvents");

        // GET /api/sessions/{id}/diffs — stub (harness diff API not yet defined)
        group.MapGet("/{id}/diffs", (string id) =>
            Results.Ok(new { diffs = Array.Empty<object>() }))
        .WithName("GetSessionDiffs");

        // GET /api/sessions/{id}/status
        group.MapGet("/{id}/status", async (string id, SessionService sessionService) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return result.Match(
                session => Results.Ok(new
                {
                    status = session.Status,
                    activityStatus = session.ActivityStatus,
                    lifecycleStatus = session.LifecycleStatus,
                    retentionStatus = session.RetentionStatus,
                    archivedAt = session.ArchivedAt
                }),
                err => err.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new { error = err.Description }),
                    _ => Results.Problem(err.Description)
                });
        })
        .WithName("GetSessionStatus");

        // POST /api/sessions/{id}/command
        group.MapPost("/{id}/command", async (string id, SendCommandApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var options = new CommandOptions
            {
                Command = req.Command,
                Arguments = req.Arguments,
                Agent = req.Agent,
                ModelId = req.Model,
            };

            var validationError = options.Validate();
            if (validationError is not null)
                return Results.BadRequest(new { error = validationError });

            var result = await orchestrator.CommandSessionAsync(id, options);
            return result.Match(_ => Results.Accepted(), err => err.ToSessionApiResult());
        })
        .WithName("SendSessionCommand");

        // DELETE /api/sessions/{id}
        group.MapDelete("/{id}", async (string id, SessionService sessionService) =>
        {
            var result = await sessionService.DeleteSessionAsync(id);
            return result.Match(
                _ => Results.NoContent(),
                error => error.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new { error = error.Description }),
                    _ => Results.Problem(error.Description)
                });
        })
        .WithName("DeleteSession");

        // PATCH /api/sessions/{id}/retention
        group.MapPatch("/{id}/retention", async (string id, UpdateSessionRetentionRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.UpdateRetentionAsync(id, req.RetentionStatus);
            return result.ToNoContentResult();
        })
        .WithName("UpdateSessionRetention");

        // PATCH /api/sessions/{id} — rename
        group.MapPatch("/{id}", async (string id, UpdateSessionTitleRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.UpdateSessionTitleAsync(id, req.Title);
            return result.ToNoContentResult();
        })
        .WithName("UpdateSessionTitle");

        // PATCH /api/sessions/{id}/project — move to project
        group.MapPatch("/{id}/project", async (string id, MoveSessionRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.MoveSessionToProjectAsync(id, req.ProjectId);
            return result.ToNoContentResult();
        })
        .WithName("MoveSessionToProject");

        return app;
    }

    /// <summary>
    /// Maps a domain <see cref="Session"/> to a <see cref="SessionListResponse"/> DTO.
    /// Workspace/instance details are embedded inline for now; Phase 4 will enrich with joins.
    /// </summary>
    private static SessionListResponse ToListResponse(
        Session s,
        HashSet<string> parentIdsWithBusyChildren,
        Dictionary<string, string> projectNamesById,
        IReadOnlyDictionary<string, SessionSourceUsage> originsBySessionId)
    {
        // Parse created_at to Unix ms for the frontend
        var createdMs = TryParseUnixMs(s.CreatedAt);
        var updatedMs = createdMs; // Sessions don't have an updated_at; use created_at

        var sessionStatus = DeriveAggregatedSessionStatus(s, parentIdsWithBusyChildren);
        var lifecycleStatus = s.LifecycleStatus ?? "running";
        var activityStatus = s.ActivityStatus;
        var origin = originsBySessionId.TryGetValue(s.Id, out var sessionSourceUsage)
            ? ToOriginDto(sessionSourceUsage)
            : null;

        return new SessionListResponse(
            InstanceId: s.InstanceId,
            WorkspaceId: s.WorkspaceId,
            WorkspaceDirectory: s.Directory,
            WorkspaceDisplayName: null,       // enriched in Phase 4
            IsolationStrategy: "existing",    // enriched in Phase 4
            SessionStatus: sessionStatus,
            Session: new SessionFleetInfo(
                Id: s.Id,
                Title: s.Title,
                Time: new SessionTime(createdMs, updatedMs)),
            InstanceStatus: "running",        // enriched in Phase 4
            ParentSessionId: s.ParentSessionId,
            SourceDirectory: null,            // enriched in Phase 4
            Branch: null,                     // enriched in Phase 4
            ActivityStatus: activityStatus,
            LifecycleStatus: lifecycleStatus,
            RetentionStatus: s.RetentionStatus,
            ArchivedAt: s.ArchivedAt,
            TypedInstanceStatus: "running",   // enriched in Phase 4
            IsHidden: s.IsHidden,
            TotalTokens: s.TotalTokens > 0 ? s.TotalTokens : null,
            TotalCost: s.TotalCost > 0 ? s.TotalCost : null,
            ProjectId: s.ProjectId,
            ProjectName: s.ProjectId is not null && projectNamesById.TryGetValue(s.ProjectId, out var projectName)
                ? projectName
                : null)
        {
            Origin = origin
        };
    }

    private static SessionOriginDto ToOriginDto(SessionSourceUsage usage) =>
        new(
            SourceType: usage.SourceType,
            Title: usage.Title,
            ResourceUrl: usage.ResourceUrl,
            ResourceId: usage.ResourceId,
            ProviderId: usage.ProviderId);

    private static SessionOriginRecordDto ToOriginRecordDto(SessionSourceUsage usage) =>
        new(
            SourceType: usage.SourceType,
            Title: usage.Title,
            ResourceUrl: usage.ResourceUrl,
            ResourceId: usage.ResourceId,
            ProviderId: usage.ProviderId,
            ActionId: usage.ActionId,
            Summary: usage.Summary,
            CreatedAt: usage.CreatedAt);

    private static string DeriveSessionStatus(Session s) =>
        s.Status switch
        {
            "stopped" => "stopped",
            "completed" => "completed",
            _ => s.ActivityStatus switch
            {
                "idle" => "idle",
                null => "idle",
                _ => "active"
            }
        };

    private static string DeriveAggregatedSessionStatus(Session session, HashSet<string> parentIdsWithBusyChildren)
    {
        if (session.Status is "stopped" or "completed" or "error" or "disconnected")
        {
            return session.Status;
        }

        return parentIdsWithBusyChildren.Contains(session.Id)
            ? "active"
            : DeriveSessionStatus(session);
    }

    private static long TryParseUnixMs(string? iso)
    {
        if (iso is null) return 0;
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        return 0;
    }
}

// ── Request record types ────────────────────────────────────────────────────

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateSessionApiRequest(
    string? Directory,
    string? Title,
    string? IsolationStrategy,
    string? Branch,
    string? HarnessType,
    string? InitialPrompt,
    SessionSourceSelection? Source,
    OnCompleteInfo? OnComplete,
    string? ProjectId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OnCompleteInfo(string NotifySessionId, string NotifyInstanceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PreviewSessionSourceApiRequest(SessionSourceSelection Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddSessionSourceApiRequest(SessionSourceSelection Source, bool Confirm);

internal sealed record SendPromptApiRequest(string Text, string? Agent, string? Model);

internal sealed record ForkSessionApiRequest(string? Title);

internal sealed record SendCommandApiRequest(
    string Command,
    string? Arguments,
    string? Agent,
    string? Model);

internal sealed record SessionOriginRecordDto(
    string SourceType,
    string? Title,
    string? ResourceUrl,
    string? ResourceId,
    string ProviderId,
    string ActionId,
    string? Summary,
    string CreatedAt);

// ── FleetError → IResult helper ─────────────────────────────────────────────

file static class SessionFleetErrorExtensions
{
    public static IResult ToSessionApiResult(this WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new { error = error.Description }),
            "General.Conflict" => Results.Conflict(new { error = error.Description }),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new { error = error.Description }),
            _ => Results.Problem(error.Description)
        };
}
