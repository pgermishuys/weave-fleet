using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Api;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Api.Endpoints;

#pragma warning disable IL2026 // RDG intercepts MapX calls in Web SDK projects making them trim-safe

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions").WithTags("Sessions");

        // GET /api/sessions?limit=&offset=&status=&projectId=&tags=
        group.MapGet("/", async (
            SessionService sessionService,
            ISessionRepository sessionRepository,
            ISessionSourceUsageRepository sessionSourceUsageRepository,
            IProjectRepository projectRepository,
            SessionActivityTracker activityTracker,
            SessionCapabilitiesResolver capabilitiesResolver,
            int limit = 100,
            int offset = 0,
            string? status = null,
            string? retentionStatus = null,
            string? projectId = null,
            string? tags = null) =>
        {
            IReadOnlyList<string>? statuses = status is not null
                ? [status]
                : null;

            IReadOnlyList<string>? tagsList = tags is not null
                ? tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null;

            var result = await sessionService.ListSessionsAsync(limit, offset, statuses, projectId, retentionStatus, tagsList);
            return await result.Match<Task<IResult>>(async sessions =>
                {
                    var projectNamesById = (await projectRepository.ListAsync())
                        .ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);

                    // Get child-to-parent mapping for all active children
                    var childToParent = await sessionRepository.GetActiveChildToParentMappingAsync();

                    // Filter to only include parents whose children are actually busy according to the activity tracker
                    var parentIdsWithBusyChildren = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (childId, parentId) in childToParent)
                    {
                        var childActivityStatus = activityTracker.GetEffectiveActivityStatus(childId);
                        if (childActivityStatus == "busy")
                        {
                            parentIdsWithBusyChildren.Add(parentId);
                        }
                    }

                    var originsBySessionId = await sessionSourceUsageRepository.GetPrimaryBySessionIdsAsync(
                        sessions.Select(session => session.Id).ToArray());

                    return Results.Ok(sessions.Select(session => ToListResponse(session, parentIdsWithBusyChildren, projectNamesById, originsBySessionId, activityTracker, capabilitiesResolver)).ToList());
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
            ISessionSourceUsageRepository sessionSourceUsageRepository,
            SessionActivityTracker activityTracker,
            SessionCapabilitiesResolver capabilitiesResolver) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return await result.Match<Task<IResult>>(
                async session =>
                {
                    var workspace = await workspaceRepository.GetByIdAsync(session.WorkspaceId);
                    var primaryOrigin = await sessionSourceUsageRepository.GetPrimaryBySessionIdAsync(session.Id);
                    var activityStatus = activityTracker.GetEffectiveActivityStatus(session.Id) ?? "idle";

                    return Results.Ok(new GetSessionResponse(
                        Id: session.Id,
                        InstanceId: session.InstanceId,
                        ParentSessionId: session.ParentSessionId,
                        WorkspaceId: session.WorkspaceId,
                        WorkspaceDirectory: workspace?.Directory ?? session.Directory,
                        WorkspaceDisplayName: workspace?.DisplayName,
                        SourceDirectory: workspace?.SourceDirectory,
                        IsolationStrategy: workspace?.IsolationStrategy ?? "existing",
                        Branch: workspace?.Branch,
                        Title: session.Title,
                        CreatedAt: session.CreatedAt,
                        StoppedAt: session.StoppedAt,
                        ActivityStatus: activityStatus,
                        LifecycleStatus: session.LifecycleStatus,
                        RetentionStatus: session.RetentionStatus,
                        ArchivedAt: session.ArchivedAt,
                        TotalTokens: session.TotalTokens > 0 ? session.TotalTokens : null,
                        TotalCost: session.TotalCost > 0 ? session.TotalCost : null,
                        HarnessType: session.HarnessType,
                        ProjectId: session.ProjectId,
                        Origin: primaryOrigin is not null ? ToOriginDto(primaryOrigin) : null,
                        Capabilities: capabilitiesResolver.Resolve(session)));
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
                ProjectId = req.ProjectId,
                Tags = req.Tags
            });
            return result.Match(
                r => Results.Ok(new CreateSessionApiResponse(
                    r.InstanceId,
                    r.WorkspaceId,
                    r.Session)),
                err => err.ToSessionApiResult());
        })
        .WithName("CreateSession");

        group.MapPost("/{id}/source-preview", async (string id, PreviewSessionSourceApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.PreviewAddSourceToSessionAsync(id, req.Source);
            return result.Match(
                envelope => Results.Ok(new PreviewSessionResponse(
                    new SessionPreviewEnvelope(
                        envelope.OriginLabel,
                        envelope.Content,
                        envelope.IsTruncated,
                        envelope.CharacterCount))),
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
        group.MapPost("/{id}/prompt", async (string id, SendPromptApiRequest req, SessionOrchestrator orchestrator, SessionService sessionService, InstanceTracker tracker, CancellationToken ct) =>
        {
            var modelResolution = await ResolveSessionModelAsync(id, req.Model, sessionService, tracker, ct);
            if (modelResolution.ErrorResult is not null)
                return modelResolution.ErrorResult;

            var attachments = req.Attachments?.Select(a => new HarnessAttachment(a.Mime, a.Filename ?? "image.png", a.Data)).ToList();
            var options = req.Agent is not null || req.Model is not null || attachments is { Count: > 0 } || req.Effort is not null
                ? new PromptOptions { Agent = req.Agent, ProviderId = modelResolution.ProviderId, ModelId = modelResolution.ModelId, Attachments = attachments, Effort = req.Effort }
                : null;
            var result = await orchestrator.PromptSessionWithReceiptAsync(id, req.Text, options, req.UserMessageId, req.CorrelationId, ct);
            return result.Match(r => Results.Ok(new SendPromptApiResponse(r.EventId, r.CorrelationId)), err => err.ToSessionApiResult());
        })
        .WithName("PromptSession");

        // POST /api/sessions/{id}/abort
        group.MapPost("/{id}/abort", async (string id, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.AbortSessionAsync(id);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("AbortSession");

        // POST /api/sessions/{id}/questions/{requestId}/answer
        group.MapPost("/{id}/questions/{requestId}/answer", async (
            string id,
            string requestId,
            QuestionAnswerApiRequest request,
            SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.AnswerQuestionAsync(id, requestId, request.Answers);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("AnswerQuestion");

        // POST /api/sessions/{id}/questions/{requestId}/reject
        group.MapPost("/{id}/questions/{requestId}/reject", async (
            string id,
            string requestId,
            SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.RejectQuestionAsync(id, requestId);
            return result.Match(_ => Results.Ok(), err => err.ToSessionApiResult());
        })
        .WithName("RejectQuestion");

        // POST /api/sessions/{id}/resume
        group.MapPost("/{id}/resume", async (
            string id,
            SessionOrchestrator orchestrator,
            SessionService sessionService,
            SessionCapabilitiesResolver capabilitiesResolver) =>
        {
            var guardResult = await GuardSessionCapabilityAsync(
                id,
                sessionService,
                capabilitiesResolver,
                capabilities => capabilities.CanResume,
                capabilities => capabilities.ResumeDisabledReason,
                "Session cannot be resumed.");
            if (guardResult is not null)
                return guardResult;

            var result = await orchestrator.ResumeSessionAsync(id);
            return result.Match(
                session => Results.Ok(new ResumeSessionApiResponse(
                    session.InstanceId,
                    session)),
                err => err.ToSessionApiResult());
        })
        .WithName("ResumeSession");

        // POST /api/sessions/{id}/stop
        group.MapPost("/{id}/stop", async (
            string id,
            SessionService sessionService,
            SessionCapabilitiesResolver capabilitiesResolver) =>
        {
            var guardResult = await GuardSessionCapabilityAsync(
                id,
                sessionService,
                capabilitiesResolver,
                capabilities => capabilities.CanStop,
                capabilities => capabilities.StopDisabledReason,
                "Session cannot be stopped.");
            if (guardResult is not null)
                return guardResult;

            var result = await sessionService.StopSessionAsync(id);
            return result.ToNoContentResult();
        })
        .WithName("StopSession");

        // POST /api/sessions/{id}/fork
        group.MapPost("/{id}/fork", async (string id, ForkSessionApiRequest req, SessionOrchestrator orchestrator) =>
        {
            var result = await orchestrator.ForkSessionAsync(id, req.Title);
            return result.Match(
                r => Results.Ok(new ForkSessionApiResponse(
                    r.InstanceId,
                    r.WorkspaceId,
                    r.Session,
                    id)),
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
                    return Results.Ok(new GetSessionMessagesApiResponse(
                        sanitizedMessages,
                        new SessionMessagesPagination(
                            page.HasMore,
                            oldest,
                            sanitizedMessages.Count)));
                },
                err => err.ToSessionApiResult());
        })
        .WithName("GetSessionMessages");

        // GET /api/sessions/{id}/diffs
        group.MapGet("/{id}/diffs", async (
            string id,
            SessionService sessionService,
            GitDiffService gitDiffService,
            CancellationToken ct) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return await result.Match<Task<IResult>>(
                async session =>
                {
                    if (string.IsNullOrWhiteSpace(session.GitRepoRoot)
                        || string.IsNullOrWhiteSpace(session.GitBaselineRef))
                    {
                        return Results.Ok(new GetSessionDiffsResponse([], Available: false));
                    }

                    var workspacePrefix = TryComputeWorkspacePrefix(session.GitRepoRoot, session.Directory);
                    if (workspacePrefix is null)
                        return Results.Ok(new GetSessionDiffsResponse([], Available: false));

                    var diffAvailability = await gitDiffService.ComputeDiffsWithAvailabilityAsync(
                        session.GitRepoRoot,
                        session.GitBaselineRef,
                        workspacePrefix,
                        ct);
                    if (!diffAvailability.Available || diffAvailability.Diffs.Count == 0)
                    {
                        return Results.Ok(new GetSessionDiffsResponse(
                            [],
                            diffAvailability.Available));
                    }

                    var diffs = await gitDiffService.ComputeDiffsWithContentAsync(
                        session.GitRepoRoot,
                        session.GitBaselineRef,
                        workspacePrefix,
                        ct);

                    return Results.Ok(new GetSessionDiffsResponse(
                        diffs.Select(ToFileDiffSummary).ToList(),
                        diffAvailability.Available));
                },
                error => Task.FromResult(error.ToSessionApiResult()));
        })
        .WithName("GetSessionDiffs");

        // GET /api/sessions/{id}/status
        group.MapGet("/{id}/status", async (string id, SessionService sessionService, SessionActivityTracker activityTracker) =>
        {
            var result = await sessionService.GetSessionAsync(id);
            return result.Match(
                session => Results.Ok(new GetSessionStatusResponse(
                    session.Status,
                    activityTracker.GetEffectiveActivityStatus(session.Id) ?? "idle",
                    session.LifecycleStatus ?? "running",
                    session.RetentionStatus,
                    session.ArchivedAt)),
                err => err.Code switch
                {
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(err.Description)),
                    _ => Results.Problem(err.Description)
                });
        })
        .WithName("GetSessionStatus");

        // POST /api/sessions/{id}/command
        group.MapPost("/{id}/command", async (string id, SendCommandApiRequest req, SessionOrchestrator orchestrator, SessionService sessionService, InstanceTracker tracker, CancellationToken ct) =>
        {
            var modelResolution = await ResolveSessionModelAsync(id, req.Model, sessionService, tracker, ct);
            if (modelResolution.ErrorResult is not null)
                return modelResolution.ErrorResult;

            var options = new CommandOptions
            {
                Command = req.Command,
                Arguments = req.Arguments,
                Agent = req.Agent,
                ProviderId = modelResolution.ProviderId,
                ModelId = modelResolution.ModelId,
            };

            var validationError = options.Validate();
            if (validationError is not null)
                return Results.BadRequest(new ErrorResponse(validationError));

            // Fire-and-forget: dispatch the command without awaiting the LLM turn.
            // Use CancellationToken.None so client disconnects don't cancel the command.
            _ = orchestrator.CommandSessionAsync(id, options, CancellationToken.None);

            return Results.Accepted();
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
                    var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
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

        // PATCH /api/sessions/{id}/tags — replace session tags
        group.MapPatch("/{id}/tags", async (string id, UpdateSessionTagsRequest req, SessionService sessionService) =>
        {
            var result = await sessionService.UpdateSessionTagsAsync(id, req.Tags);
            return result.Match(
                session => Results.Ok(session),
                error => error.ToSessionApiResult());
        })
        .WithName("UpdateSessionTags");

        // GET /api/sessions/{id}/models — session-scoped model list
        group.MapGet("/{id}/models", async (string id, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.GetSessionModelsAsync(id, ct);
            return result.Match(
                providers =>
                {
                    var items = providers.Select(p => new InstanceProviderItem(
                        p.Id,
                        p.Name ?? p.Id,
                        p.Models.Select(m => new InstanceModelItem(m.Id, m.Name ?? m.Id, m.Variants)).ToList())).ToList();
                    return Results.Ok(items);
                },
                err => err.ToSessionApiResult());
        })
        .WithName("GetSessionModels");

        // GET /api/sessions/{id}/commands — session-scoped commands list
        group.MapGet("/{id}/commands", async (string id, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.GetSessionCommandsAsync(id, ct);
            return result.Match(
                commands =>
                {
                    var items = commands.Select(c => new InstanceCommandItem(c.Name, c.Description)).ToList();
                    return Results.Ok(new InstanceCommandsResponse(id, items));
                },
                err => err.ToSessionApiResult());
        })
        .WithName("GetSessionCommands");

        // GET /api/sessions/{id}/agents — session-scoped agents list
        group.MapGet("/{id}/agents", async (string id, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.GetSessionAgentsAsync(id, ct);
            return result.Match(
                agents =>
                {
                    var items = agents.Select(a => new InstanceAgentItem(
                        a.Name,
                        a.Description,
                        a.Mode ?? "agent",
                        a.Hidden,
                        a.ModelProviderId is not null
                            ? new InstanceAgentModelRef(a.ModelProviderId, a.ModelId ?? string.Empty)
                            : null)).ToList();
                    return Results.Ok(new InstanceAgentsResponse(id, items));
                },
                err => err.ToSessionApiResult());
        })
        .WithName("GetSessionAgents");

        // GET /api/sessions/{id}/find/files?q= — session-scoped file search
        group.MapGet("/{id}/find/files", async (string id, string? q, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.Ok(new InstanceFilesResponse(id, Array.Empty<string>()));

            var result = await orchestrator.FindSessionFilesAsync(id, q, ct);
            return result.Match(
                files => Results.Ok(new InstanceFilesResponse(id, files.ToArray())),
                err => err.ToSessionApiResult());
        })
        .WithName("FindSessionFiles");

        // GET /api/sessions/{id}/files/browse?path= — session directory browser
        group.MapGet("/{id}/files/browse", async (string id, string? path, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.BrowseSessionDirectoryAsync(id, path, ct);
            return result.Match(
                browseResult => Results.Ok(new BrowseSessionDirectoryResponse(
                    browseResult.Entries.Select(e => new BrowseEntryDto(e.Name, e.RelativePath, e.IsDirectory)).ToList(),
                    browseResult.CurrentPath)),
                err => err.ToSessionApiResult());
        })
        .Produces<BrowseSessionDirectoryResponse>(200)
        .Produces(404)
        .Produces(400)
        .WithName("BrowseSessionDirectory");

        // GET /api/sessions/{id}/files/content?path= — read session file content
        group.MapGet("/{id}/files/content", async (string id, string? path, SessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.ReadSessionFileAsync(id, path, ct);
            return result.Match(
                fileResult => Results.Ok(new ReadSessionFileResponse(
                    fileResult.Path,
                    fileResult.Content,
                    fileResult.IsBinary,
                    fileResult.IsTruncated)),
                err => err.ToSessionApiResult());
        })
        .Produces<ReadSessionFileResponse>(200)
        .Produces(404)
        .Produces(400)
        .WithName("ReadSessionFile");

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
        IReadOnlyDictionary<string, SessionSourceUsage> originsBySessionId,
        SessionActivityTracker activityTracker,
        SessionCapabilitiesResolver capabilitiesResolver)
    {
        // Parse created_at to Unix ms for the frontend
        var createdMs = TryParseUnixMs(s.CreatedAt);
        var updatedMs = createdMs; // Sessions don't have an updated_at; use created_at

        // Prefer the tracker's derived effective activity status (child busy → parent busy)
        // over the DB-persisted value, which may lag real-time state.
        var activityStatus = activityTracker.GetEffectiveActivityStatus(s.Id) ?? "idle";
        var sessionStatus = DeriveAggregatedSessionStatus(s, activityStatus, parentIdsWithBusyChildren);
        var lifecycleStatus = s.LifecycleStatus ?? "running";

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
                Time: new SessionTime(createdMs, updatedMs),
                Tags: s.Tags ?? []),
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
                : null,
            HarnessType: s.HarnessType,
            Capabilities: capabilitiesResolver.Resolve(s),
            Tags: s.Tags ?? [])
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

    private static async Task<IResult?> GuardSessionCapabilityAsync(
        string id,
        SessionService sessionService,
        SessionCapabilitiesResolver capabilitiesResolver,
        Func<WeaveFleet.Domain.DTOs.SessionActionCapabilities, bool> isAllowed,
        Func<WeaveFleet.Domain.DTOs.SessionActionCapabilities, string?> getDisabledReason,
        string fallbackDisabledReason)
    {
        var sessionResult = await sessionService.GetSessionAsync(id);
        if (sessionResult.IsFailure)
            return sessionResult.Error.ToSessionApiResult();

        var capabilities = capabilitiesResolver.Resolve(sessionResult.Value);
        if (isAllowed(capabilities))
            return null;

        return Results.Conflict(new ErrorResponse(getDisabledReason(capabilities) ?? fallbackDisabledReason));
    }

    private static string DeriveSessionStatus(Session s, string activityStatus) =>
        s.Status switch
        {
            "stopped" => "stopped",
            "completed" => "completed",
            _ => activityStatus switch
            {
                "idle" => "idle",
                _ => "active"
            }
        };

    private static string DeriveAggregatedSessionStatus(Session session, string activityStatus, HashSet<string> parentIdsWithBusyChildren)
    {
        if (session.Status is "stopped" or "completed" or "error" or "disconnected")
        {
            return session.Status;
        }

        return parentIdsWithBusyChildren.Contains(session.Id)
            ? "active"
            : DeriveSessionStatus(session, activityStatus);
    }

    private static FileDiffSummary ToFileDiffSummary(WeaveFleet.Application.Services.FileDiffContent diff) =>
        new(
            File: diff.Path,
            Status: diff.Status,
            Additions: diff.Additions,
            Deletions: diff.Deletions,
            Before: diff.Before,
            After: diff.After,
            IsBinary: diff.IsBinary,
            IsTruncated: diff.IsTruncated);

    private static string? TryComputeWorkspacePrefix(string repoRoot, string sessionDirectory)
    {
        if (string.IsNullOrWhiteSpace(repoRoot))
            return null;

        if (string.IsNullOrWhiteSpace(sessionDirectory))
            return string.Empty;

        try
        {
            var repoRootFullPath = Path.GetFullPath(repoRoot);
            var sessionDirectoryFullPath = Path.GetFullPath(sessionDirectory);

            if (!IsSameOrChildPath(sessionDirectoryFullPath, repoRootFullPath))
                return null;

            if (PathsEqual(sessionDirectoryFullPath, repoRootFullPath))
                return string.Empty;

            return Path.GetRelativePath(repoRootFullPath, sessionDirectoryFullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Trim('/');
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var root = TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var candidate = TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        if (PathsEqual(candidate, root))
            return true;

        return candidate.StartsWith(EnsureEndingDirectorySeparator(root), PathStringComparison);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            TrimEndingDirectorySeparator(left),
            TrimEndingDirectorySeparator(right),
            PathStringComparison);

    private static string EnsureEndingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string TrimEndingDirectorySeparator(string path) =>
        Path.GetPathRoot(path) == path
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathStringComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static long TryParseUnixMs(string? iso)
    {
        if (iso is null) return 0;
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
        return 0;
    }

    /// <summary>
    /// Shared helper (no origin enrichment).
    /// </summary>
    internal static SessionListResponse ToListResponseNoOrigin(
        Session s,
        HashSet<string> parentIdsWithBusyChildren,
        Dictionary<string, string> projectNamesById,
        SessionActivityTracker activityTracker,
        SessionCapabilitiesResolver capabilitiesResolver)
    {
        var createdMs = TryParseUnixMs(s.CreatedAt);
        var updatedMs = createdMs;
        var activityStatus = activityTracker.GetEffectiveActivityStatus(s.Id) ?? "idle";
        var sessionStatus = DeriveAggregatedSessionStatus(s, activityStatus, parentIdsWithBusyChildren);
        var lifecycleStatus = s.LifecycleStatus ?? "running";

        return new SessionListResponse(
            InstanceId: s.InstanceId,
            WorkspaceId: s.WorkspaceId,
            WorkspaceDirectory: s.Directory,
            WorkspaceDisplayName: null,
            IsolationStrategy: "existing",
            SessionStatus: sessionStatus,
            Session: new SessionFleetInfo(
                Id: s.Id,
                Title: s.Title,
                Time: new SessionTime(createdMs, updatedMs),
                Tags: s.Tags ?? []),
            InstanceStatus: "running",
            ParentSessionId: s.ParentSessionId,
            SourceDirectory: null,
            Branch: null,
            ActivityStatus: activityStatus,
            LifecycleStatus: lifecycleStatus,
            RetentionStatus: s.RetentionStatus,
            ArchivedAt: s.ArchivedAt,
            TypedInstanceStatus: "running",
            IsHidden: s.IsHidden,
            TotalTokens: s.TotalTokens > 0 ? s.TotalTokens : null,
            TotalCost: s.TotalCost > 0 ? s.TotalCost : null,
            ProjectId: s.ProjectId,
            ProjectName: s.ProjectId is not null && projectNamesById.TryGetValue(s.ProjectId, out var projectName)
                ? projectName
                : null,
            HarnessType: s.HarnessType,
            Capabilities: capabilitiesResolver.Resolve(s),
            Tags: s.Tags ?? []);
    }

    private static async Task<ModelResolutionResult> ResolveSessionModelAsync(
        string sessionId,
        ModelRef? model,
        SessionService sessionService,
        InstanceTracker tracker,
        CancellationToken ct)
    {
        // No model in the request → fall back to the session's persisted selection so that
        // a SPA refresh (which loses local state) doesn't silently drop the model down to
        // the harness default. SessionOrchestrator.PromptSessionAsync writes the selection
        // on every successful prompt that resolved to a concrete (provider, model) pair.
        if (model is null)
        {
            var stored = await sessionService.GetSessionAsync(sessionId);
            if (stored.IsSuccess
                && stored.Value.SelectedProviderId is { Length: > 0 } sp
                && stored.Value.SelectedModelId is { Length: > 0 } sm)
            {
                return new ModelResolutionResult(sp, sm, null);
            }
            return ModelResolutionResult.Empty;
        }

        var sessionResult = await sessionService.GetSessionAsync(sessionId);
        if (sessionResult.IsFailure)
            return new ModelResolutionResult(null, null, sessionResult.Error.ToSessionApiResult());

        var instance = tracker.Get(sessionResult.Value.InstanceId);
        if (instance is null)
            return new ModelResolutionResult(null, null, Results.NotFound(new ErrorResponse($"Instance '{sessionResult.Value.InstanceId}' not found or not running.")));

        var providers = await instance.GetProvidersAsync(ct);
        return ModelRef.TryResolve(model, providers, out var resolved, out var error)
            ? new ModelResolutionResult(resolved.ProviderId, resolved.ModelId, null)
            : new ModelResolutionResult(null, null, Results.BadRequest(new ErrorResponse(error!)));
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
    string? ProjectId,
    List<string>? Tags);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OnCompleteInfo(string NotifySessionId, string NotifyInstanceId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PreviewSessionSourceApiRequest(SessionSourceSelection Source);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddSessionSourceApiRequest(SessionSourceSelection Source, bool Confirm);

internal sealed record SendPromptApiRequest(
    string Text,
    string? Agent,
    ModelRef? Model,
    ImageAttachmentDto[]? Attachments,
    string? UserMessageId,
    string? CorrelationId,
    string? Effort);

internal sealed record SendPromptApiResponse(long? EventId, string CorrelationId);

internal sealed record ImageAttachmentDto(string Mime, string? Filename, string Data);

internal sealed record ForkSessionApiRequest(string? Title);

internal sealed record SendCommandApiRequest(
    string Command,
    string? Arguments,
    string? Agent,
    ModelRef? Model);

internal sealed record QuestionAnswerApiRequest(IReadOnlyList<IReadOnlyList<string>> Answers);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record UpdateSessionTagsRequest(List<string> Tags);

internal sealed record SessionOriginRecordDto(
    string SourceType,
    string? Title,
    string? ResourceUrl,
    string? ResourceId,
    string ProviderId,
    string ActionId,
    string? Summary,
    string CreatedAt);

internal sealed record ModelResolutionResult(string? ProviderId, string? ModelId, IResult? ErrorResult)
{
    public static ModelResolutionResult Empty { get; } = new(null, null, null);
}

// ── ModelRef — accepts either a plain string or { providerID, modelID } object ──

/// <summary>
/// Represents a model reference that can be deserialized from either a plain string
/// (backward-compat: split on first '/') or an object <c>{ providerID, modelID }</c>.
/// </summary>
[JsonConverter(typeof(ModelRefJsonConverter))]
internal sealed record ModelRef
{
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? LegacyValue { get; init; }

    /// <summary>Creates a <see cref="ModelRef"/> from an object payload.</summary>
    public static ModelRef FromObject(string? providerId, string? modelId) =>
        new() { ProviderId = providerId, ModelId = modelId };

    /// <summary>Creates a <see cref="ModelRef"/> from a backward-compat plain string.</summary>
    public static ModelRef FromString(string value) =>
        new() { LegacyValue = value };

    public static bool TryResolve(
        ModelRef? model,
        IReadOnlyList<ProviderInfo> providers,
        out ResolvedModelRef resolved,
        out string? error)
    {
        if (model is null)
        {
            resolved = ResolvedModelRef.Empty;
            error = null;
            return true;
        }

        return TryResolveCore(model, providers, out resolved, out error);
    }

    private static bool TryResolveCore(
        ModelRef model,
        IReadOnlyList<ProviderInfo> providers,
        out ResolvedModelRef resolved,
        out string? error)
    {
        if (model.LegacyValue is null)
            return TryResolveStructuredValue(model.ProviderId, model.ModelId, providers, out resolved, out error);

        return TryResolveLegacyValue(model.LegacyValue, providers, out resolved, out error);
    }

    private static bool TryResolveStructuredValue(
        string? providerId,
        string? modelId,
        IReadOnlyList<ProviderInfo> providers,
        out ResolvedModelRef resolved,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId))
        {
            resolved = ResolvedModelRef.Empty;
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId))
        {
            resolved = ResolvedModelRef.Empty;
            error = "Model must include both providerID and modelID.";
            return false;
        }

        var exists = providers.Any(provider =>
            string.Equals(provider.Id, providerId, StringComparison.Ordinal)
            && provider.Models.Any(model => string.Equals(model.Id, modelId, StringComparison.Ordinal)));

        if (!exists)
        {
            resolved = ResolvedModelRef.Empty;
            error = $"Unknown model '{providerId}/{modelId}'.";
            return false;
        }

        resolved = new ResolvedModelRef(providerId, modelId);
        error = null;
        return true;
    }

    private static bool TryResolveLegacyValue(
        string legacyValue,
        IReadOnlyList<ProviderInfo> providers,
        out ResolvedModelRef resolved,
        out string? error)
    {
        var exactMatches = providers
            .SelectMany(static provider => provider.Models.Select(model => new ResolvedModelRef(provider.Id, model.Id)))
            .Where(candidate => string.Equals($"{candidate.ProviderId}/{candidate.ModelId}", legacyValue, StringComparison.Ordinal))
            .ToArray();

        if (exactMatches.Length == 1)
        {
            resolved = exactMatches[0];
            error = null;
            return true;
        }

        if (exactMatches.Length > 1)
        {
            resolved = ResolvedModelRef.Empty;
            error = $"Model '{legacyValue}' is ambiguous. Send model as {{ providerID, modelID }}.";
            return false;
        }

        var rawMatches = providers
            .SelectMany(static provider => provider.Models.Select(model => new ResolvedModelRef(provider.Id, model.Id)))
            .Where(candidate => string.Equals(candidate.ModelId, legacyValue, StringComparison.Ordinal))
            .ToArray();

        if (rawMatches.Length == 1)
        {
            resolved = rawMatches[0];
            error = null;
            return true;
        }

        if (rawMatches.Length > 1)
        {
            resolved = ResolvedModelRef.Empty;
            error = $"Model '{legacyValue}' is ambiguous. Send model as {{ providerID, modelID }}.";
            return false;
        }

        resolved = ResolvedModelRef.Empty;
        error = $"Unknown model '{legacyValue}'. Send model as {{ providerID, modelID }}.";
        return false;
    }
}

internal sealed record ResolvedModelRef(string? ProviderId, string? ModelId)
{
    public static ResolvedModelRef Empty { get; } = new(null, null);
}

internal sealed class ModelRefJsonConverter : JsonConverter<ModelRef>
{
    public override ModelRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ModelRef.FromString(reader.GetString()!);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected string or object for model.");

        string? providerId = null;
        string? modelId = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var propName = reader.GetString();
            reader.Read();
            if (string.Equals(propName, "providerID", StringComparison.Ordinal))
                providerId = reader.GetString();
            else if (string.Equals(propName, "modelID", StringComparison.Ordinal))
                modelId = reader.GetString();
            else
                reader.Skip();
        }

        return ModelRef.FromObject(providerId, modelId);
    }

    public override void Write(Utf8JsonWriter writer, ModelRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("providerID", value.ProviderId);
        writer.WriteString("modelID", value.ModelId);
        writer.WriteEndObject();
    }
}

// ── Browse directory response types ────────────────────────────────────────────

internal sealed record BrowseSessionDirectoryResponse(
    IReadOnlyList<BrowseEntryDto> Entries,
    string CurrentPath);

internal sealed record BrowseEntryDto(
    string Name,
    string RelativePath,
    bool IsDirectory);

// ── Read file response types ───────────────────────────────────────────────────

internal sealed record ReadSessionFileResponse(
    string Path,
    string? Content,
    bool IsBinary,
    bool IsTruncated);

// ── FleetError → IResult helper ─────────────────────────────────────────────

file static class SessionFleetErrorExtensions
{
    public static IResult ToSessionApiResult(this WeaveFleet.Domain.Common.FleetError error) =>
        error.Code switch
        {
            var c when c.EndsWith(".NotFound", StringComparison.Ordinal) => Results.NotFound(new ErrorResponse(error.Description)),
            "General.Conflict" => Results.Conflict(new ErrorResponse(error.Description)),
            var c when c.StartsWith("Validation.", StringComparison.Ordinal) => Results.BadRequest(new ErrorResponse(error.Description)),
            _ => Results.Problem(error.Description)
        };
}
#pragma warning restore IL2026
