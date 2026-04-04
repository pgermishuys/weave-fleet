using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// High-level coordinator for session lifecycle operations.
/// Bridges workspace creation, harness spawning, DB persistence, and harness communication.
/// </summary>
public sealed partial class SessionOrchestrator(
    WorkspaceService workspaceService,
    InstanceService instanceService,
    IHarnessRegistry harnessRegistry,
    InstanceTracker instanceTracker,
    ISessionRepository sessionRepository,
    ISessionCallbackRepository sessionCallbackRepository,
    IProjectRepository projectRepository,
    IEventBroadcaster eventBroadcaster,
    IAnalyticsCollector analyticsCollector,
    ILogger<SessionOrchestrator> logger)
{
    private const string DefaultHarnessType = "opencode";
    private const string ScratchProjectName = "Scratch";

    // ── Create ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Full create-session flow:
    /// 1. Create or reuse workspace
    /// 2. Spawn harness instance
    /// 3. Persist instance + session records
    /// 4. Optionally register a completion callback
    /// </summary>
    public async Task<Result<CreateSessionResult>> CreateSessionAsync(
        CreateSessionRequest request,
        CancellationToken ct = default)
    {
        // Resolve harness
        var harnessType = request.HarnessType ?? DefaultHarnessType;
        var harness = harnessRegistry.GetByType(harnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", harnessType);

        // Resolve or default project
        var projectId = request.ProjectId ?? await ResolveScratchProjectIdAsync();

        // Look up project name for analytics context (best-effort)
        string? projectName = null;
        if (projectId is not null)
        {
            var projects = await projectRepository.ListAsync();
            projectName = projects.FirstOrDefault(p => p.Id == projectId)?.Name;
        }

        // 1. Create workspace
        var workspaceResult = await workspaceService.CreateWorkspaceAsync(
            request.Directory,
            request.IsolationStrategy ?? "existing",
            request.Branch);
        if (workspaceResult.IsFailure)
            return workspaceResult.Error;

        var workspace = workspaceResult.Value;

        // 2. Spawn harness instance
        var sessionId = Guid.NewGuid().ToString();
        IHarnessInstance harnessInstance;
        try
        {
            harnessInstance = await harness.SpawnAsync(new HarnessSpawnOptions
            {
                SessionId = sessionId,
                WorkingDirectory = workspace.Directory,
                InitialPrompt = request.InitialPrompt,
                Branch = request.Branch,
                ProjectId = projectId,
                ProjectName = projectName
            }, ct);
        }
        catch (Exception ex)
        {
            LogSpawnFailed(ex, harnessType);
            return FleetError.Unexpected;
        }

        // 3. Persist instance
        var instanceResult = await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,           // port is harness-implementation detail; 0 = unknown
            pid: null,
            directory: workspace.Directory,
            url: string.Empty);
        if (instanceResult.IsFailure)
        {
            await SafeStopAsync(harnessInstance, ct);
            return instanceResult.Error;
        }

        // Track in-memory handle
        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);

        // 4. Persist session
        var session = new Session
        {
            Id = sessionId,
            WorkspaceId = workspace.Id,
            InstanceId = harnessInstance.InstanceId,
            ProjectId = projectId,
            OpencodeSessionId = harnessInstance.InstanceId,
            Title = request.Title ?? "Untitled",
            Status = "active",
            Directory = workspace.Directory,
            CreatedAt = DateTime.UtcNow.ToString("O")
        };

        await sessionRepository.InsertAsync(session);
        LogSessionCreated(session.Id, workspace.Id, harnessInstance.InstanceId);

        // Emit analytics snapshot for the new session
        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: session.Id,
            ParentSessionId: null,
            ProjectId: projectId,
            ProjectName: projectName,
            WorkspaceDirectory: workspace.Directory,
            Title: session.Title,
            Status: "active",
            TotalTokens: 0,
            TotalCost: 0,
            TotalEstimatedCost: 0,
            MessageCount: 0,
            ModelIds: [],
            CreatedAt: DateTimeOffset.UtcNow,
            EndedAt: null,
            DurationSeconds: null));

        // Broadcast session_created event
        await eventBroadcaster.BroadcastAsync("sessions", "session_created", new
        {
            sessionId = session.Id,
            instanceId = harnessInstance.InstanceId,
            workspaceId = workspace.Id,
            title = session.Title,
            projectId = session.ProjectId
        }, ct);

        // 5. Register callback (optional)
        if (request.OnCompleteTargetSessionId is not null && request.OnCompleteTargetInstanceId is not null)
        {
            var callback = new SessionCallback
            {
                Id = Guid.NewGuid().ToString(),
                SourceSessionId = session.Id,
                TargetSessionId = request.OnCompleteTargetSessionId,
                TargetInstanceId = request.OnCompleteTargetInstanceId,
                Status = "pending",
                CreatedAt = DateTime.UtcNow.ToString("O")
            };
            await sessionCallbackRepository.InsertAsync(callback);
        }

        return new CreateSessionResult(session, harnessInstance.InstanceId, workspace.Id);
    }

    // ── Resume ─────────────────────────────────────────────────────────────────

    public async Task<Result<Session>> ResumeSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        var workspaceResult = await workspaceService.GetWorkspaceDirectoryAsync(session.WorkspaceId);
        if (workspaceResult.IsFailure)
            return workspaceResult.Error;

        var harness = harnessRegistry.GetByType(DefaultHarnessType);
        if (harness is null)
            return FleetError.NotFoundFor("Harness", DefaultHarnessType);

        IHarnessInstance harnessInstance;
        try
        {
            harnessInstance = await harness.SpawnAsync(new HarnessSpawnOptions
            {
                SessionId = session.Id,
                WorkingDirectory = workspaceResult.Value
            }, ct);
        }
        catch (Exception ex)
        {
            LogSpawnFailed(ex, DefaultHarnessType);
            return FleetError.Unexpected;
        }

        await instanceService.RegisterInstanceAsync(
            id: harnessInstance.InstanceId,
            port: 0,
            pid: null,
            directory: workspaceResult.Value,
            url: string.Empty);

        instanceTracker.Register(harnessInstance.InstanceId, harnessInstance);
        await sessionRepository.UpdateForResumeAsync(session.Id, harnessInstance.InstanceId);

        session.InstanceId = harnessInstance.InstanceId;
        return session;
    }

    // ── Fork ───────────────────────────────────────────────────────────────────

    public async Task<Result<CreateSessionResult>> ForkSessionAsync(
        string parentId,
        string? title = null,
        CancellationToken ct = default)
    {
        var parent = await sessionRepository.GetByIdAsync(parentId);
        if (parent is null)
            return FleetError.NotFoundFor(nameof(Session), parentId);

        // Fork reuses same workspace directory (no isolation)
        return await CreateSessionAsync(new CreateSessionRequest
        {
            Directory = parent.Directory,
            Title = title ?? $"Fork of {parent.Title}",
            ProjectId = parent.ProjectId,
            HarnessType = DefaultHarnessType,
            IsolationStrategy = "existing"
        }, ct);
    }

    // ── Prompt / Abort ─────────────────────────────────────────────────────────

    public async Task<Result<Unit>> PromptSessionAsync(
        string id,
        string text,
        PromptOptions? options = null,
        CancellationToken ct = default)
    {
        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.SendPromptAsync(text, options, ct);
        return Unit.Value;
    }

    public async Task<Result<Unit>> AbortSessionAsync(string id, CancellationToken ct = default)
    {
        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        await instanceResult.Value.AbortAsync(ct);
        return Unit.Value;
    }

    // ── Messages / Diffs ───────────────────────────────────────────────────────

    public async Task<Result<MessagePage>> GetSessionMessagesAsync(
        string id,
        MessageQuery? query = null,
        CancellationToken ct = default)
    {
        var instanceResult = await GetLiveInstanceAsync(id);
        if (instanceResult.IsFailure)
            return instanceResult.Error;

        try
        {
            var page = await instanceResult.Value.GetMessagesAsync(query, ct);
            return Result.Success(page);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGetMessagesFailed(ex, id);
            return FleetError.Unexpected;
        }
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    public async Task<Result<Unit>> DeleteSessionAsync(string id, CancellationToken ct = default)
    {
        var session = await sessionRepository.GetByIdAsync(id);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), id);

        // Stop live instance if running
        var liveInstance = instanceTracker.Get(session.InstanceId);
        if (liveInstance is not null)
        {
            await SafeStopAsync(liveInstance, ct);
            instanceTracker.Remove(session.InstanceId);
        }

        await instanceService.UpdateInstanceStatusAsync(
            session.InstanceId, "stopped", DateTime.UtcNow.ToString("O"));

        await sessionRepository.DeleteAsync(id);

        // Emit analytics snapshot marking session as stopped
        analyticsCollector.AcceptSessionSnapshot(new SessionSnapshotData(
            SessionId: id,
            ParentSessionId: null,
            ProjectId: session.ProjectId,
            ProjectName: null,
            WorkspaceDirectory: session.Directory,
            Title: session.Title,
            Status: "stopped",
            TotalTokens: session.TotalTokens,
            TotalCost: session.TotalCost,
            TotalEstimatedCost: 0,
            MessageCount: 0,
            ModelIds: [],
            CreatedAt: DateTimeOffset.Parse(session.CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
            EndedAt: DateTimeOffset.UtcNow,
            DurationSeconds: null));

        // Broadcast session_stopped event
        await eventBroadcaster.BroadcastAsync("sessions", "session_stopped", new
        {
            sessionId = id
        }, ct);

        return Unit.Value;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task<Result<IHarnessInstance>> GetLiveInstanceAsync(string sessionId)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId);
        if (session is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);

        var instance = instanceTracker.Get(session.InstanceId);
        if (instance is null)
            return FleetError.NotFoundFor("Instance", session.InstanceId);

        return Result.Success<IHarnessInstance>(instance);
    }

    private async Task<string?> ResolveScratchProjectIdAsync()
    {
        // Find the Scratch project by name convention
        var projects = await projectRepository.ListAsync();
        return projects.FirstOrDefault(p =>
            p.Name.Equals(ScratchProjectName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    private async Task SafeStopAsync(IHarnessInstance instance, CancellationToken ct)
    {
        try { await instance.StopAsync(ct); }
        catch (Exception ex) { LogStopFailed(ex, instance.InstanceId); }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to spawn harness {HarnessType}")]
    private partial void LogSpawnFailed(Exception ex, string harnessType);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} created: workspace={WorkspaceId} instance={InstanceId}")]
    private partial void LogSessionCreated(string sessionId, string workspaceId, string instanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stop instance {InstanceId}")]
    private partial void LogStopFailed(Exception ex, string instanceId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to retrieve messages for session {SessionId} — returning error result")]
    private partial void LogGetMessagesFailed(Exception ex, string sessionId);
}

// ── Request / Result DTOs ──────────────────────────────────────────────────────

/// <summary>Input for creating a new session.</summary>
public sealed record CreateSessionRequest
{
    public required string Directory { get; init; }
    public string? Title { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
    public string? HarnessType { get; init; }
    public string? ProjectId { get; init; }
    public string? InitialPrompt { get; init; }
    /// <summary>If set, registers a completion callback to resume this target session.</summary>
    public string? OnCompleteTargetSessionId { get; init; }
    public string? OnCompleteTargetInstanceId { get; init; }
}

/// <summary>Result of a successful <see cref="SessionOrchestrator.CreateSessionAsync"/> call.</summary>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId);
