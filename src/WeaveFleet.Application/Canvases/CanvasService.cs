using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Canvases;

/// <summary>
/// The <c>session.pages</c> event on the sessions topic: the session's open pages after one was shown, reopened or
/// closed, so its row in the session list stays current. Same fields as the list item's.
/// </summary>
public sealed record SessionPagesChanged(
    string SessionId,
    int PageCount,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? LastPageShownAt);

public sealed class CanvasService(
    ICanvasRepository canvasRepository,
    IEventBroadcaster eventBroadcaster,
    IUserContext userContext) : ICanvasService
{
    // Writers only collide when the user and the agent change the same canvas at the same moment.
    private const int MaxWriteAttempts = 5;
    private const string ReopenedSummary = "reopened";

    /// <summary>The event carrying a session's open pages after they changed (<see cref="SessionPagesChanged"/>).</summary>
    public const string PagesChangedEvent = "session.pages";

    public async Task<IReadOnlyList<CanvasListItem>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var canvases = await canvasRepository.ListBySessionIdAsync(sessionId);
        // Diagram and sequence canvases were retired; migration 064 deletes them, and this keeps any that slip through off the panel.
        return canvases.Where(canvas => CanvasKinds.IsKnown(canvas.Kind)).Select(canvas => new CanvasListItem(canvas)).ToList();
    }

    public async Task<Canvas?> GetAsync(string sessionId, string canvasId, CancellationToken ct = default)
        => await canvasRepository.GetByIdAsync(sessionId, canvasId) is { } canvas && CanvasKinds.IsKnown(canvas.Kind) ? canvas : null;

    public async Task<CanvasResult<CanvasOutcome>> OpenAsync(
        string sessionId,
        string kind,
        string title,
        JsonNode? state,
        CancellationToken ct = default)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > CanvasLimits.MaxTitleLength)
            return CanvasResult.Fail<CanvasOutcome>(CanvasErrorKind.Invalid, $"\"title\" must be 1-{CanvasLimits.MaxTitleLength} characters.");

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var existing = await canvasRepository.GetByTitleAsync(sessionId, title);
            if (existing is null)
                return await CreateAsync(sessionId, kind, title, state, ct);

            if (existing.Kind != kind)
            {
                return CanvasResult.Fail<CanvasOutcome>(
                    CanvasErrorKind.Invalid,
                    $"{CanvasText.CanvasName(existing)} is already a {existing.Kind} canvas. Use another title.");
            }

            var ops = CanvasOps.Replace(kind, existing.StateJson, state);
            if (!ops.IsSuccess)
                return CanvasResult.Fail<CanvasOutcome>(ops.Error);

            var wasClosed = existing.ClosedAt is not null;
            if (ops.Value.Count == 0)
            {
                if (wasClosed)
                    await canvasRepository.SetClosedAtAsync(sessionId, existing.Id, null);
                await canvasRepository.MarkAgentSeenAsync(sessionId, existing.Id, existing.Version);
                var current = await canvasRepository.GetByIdAsync(sessionId, existing.Id) ?? existing;
                if (wasClosed)
                {
                    await BroadcastUpdatedAsync(current, CanvasActor.Agent, ReopenedSummary, ct);
                    await BroadcastPagesAsync(current, ct);
                }
                await BroadcastFocusedAsync(current, ct);
                return CanvasResult.Ok(new CanvasOutcome(current, "no changes", Created: false));
            }

            var change = CanvasOps.Apply(kind, existing.StateJson, ops.Value);
            if (!change.IsSuccess)
                return CanvasResult.Fail<CanvasOutcome>(change.Error);

            var updated = await TryWriteAsync(existing, change.Value, CanvasActor.Agent, agentSeenVersion: existing.Version + 1);
            if (updated is null)
                continue;

            if (wasClosed)
            {
                await canvasRepository.SetClosedAtAsync(sessionId, existing.Id, null);
                updated.ClosedAt = null;
            }

            await BroadcastUpdatedAsync(updated, CanvasActor.Agent, change.Value.Summary, ct);
            await BroadcastPagesAsync(updated, ct);
            await BroadcastFocusedAsync(updated, ct);
            return CanvasResult.Ok(new CanvasOutcome(updated, change.Value.Summary, Created: false));
        }

        throw new InvalidOperationException($"Canvas \"{title}\" kept changing while it was being opened.");
    }

    public async Task<CanvasResult<Canvas>> UpdatePageAsync(string sessionId, string canvasId, Func<PageState, PageState?> change, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var existing = await canvasRepository.GetByIdAsync(sessionId, canvasId);
            if (existing is not { Kind: CanvasKinds.Page })
                return CanvasResult.Fail<Canvas>(CanvasErrorKind.NotFound, $"There's no page canvas {canvasId} in this session.");
            if (change(PageState.Parse(existing.StateJson)) is not { } page)
                return CanvasResult.Ok(existing);

            var applied = CanvasOps.Apply(CanvasKinds.Page, existing.StateJson, [new ShowPageOp(page)]);
            if (!applied.IsSuccess)
                return CanvasResult.Fail<Canvas>(applied.Error);

            var updated = await TryWriteAsync(existing, applied.Value, CanvasActor.Agent, existing.AgentSeenVersion);
            if (updated is null)
                continue;

            await BroadcastUpdatedAsync(updated, CanvasActor.Agent, applied.Value.Summary, ct);
            return CanvasResult.Ok(updated);
        }

        throw new InvalidOperationException($"Canvas {canvasId} kept changing while Fleet updated its page.");
    }

    public async Task<CanvasResult<string>> ReadAsync(string sessionId, string canvasId, CancellationToken ct = default)
    {
        var canvas = await GetAsync(sessionId, canvasId, ct);
        if (canvas is null)
            return CanvasResult.Fail<string>(NotFound(canvasId));

        await canvasRepository.MarkAgentSeenAsync(sessionId, canvasId, canvas.Version);
        return CanvasResult.Ok(CanvasText.RenderFull(canvas));
    }

    public async Task<CanvasResult<Canvas>> FocusAsync(string sessionId, string canvasId, CancellationToken ct = default)
    {
        var canvas = await canvasRepository.GetByIdAsync(sessionId, canvasId);
        if (canvas is null)
            return CanvasResult.Fail<Canvas>(NotFound(canvasId));

        if (canvas.ClosedAt is not null)
        {
            await canvasRepository.SetClosedAtAsync(sessionId, canvasId, null);
            canvas.ClosedAt = null;
            await BroadcastUpdatedAsync(canvas, CanvasActor.Agent, ReopenedSummary, ct);
            await BroadcastPagesAsync(canvas, ct);
        }

        await BroadcastFocusedAsync(canvas, ct);
        return CanvasResult.Ok(canvas);
    }

    public async Task<CanvasResult<Canvas>> CloseAsync(string sessionId, string canvasId, CancellationToken ct = default)
    {
        var canvas = await canvasRepository.GetByIdAsync(sessionId, canvasId);
        if (canvas is null)
            return CanvasResult.Fail<Canvas>(NotFound(canvasId));
        if (canvas.ClosedAt is not null)
            return CanvasResult.Ok(canvas);

        canvas.ClosedAt = DateTime.UtcNow.ToString("O");
        await canvasRepository.SetClosedAtAsync(sessionId, canvasId, canvas.ClosedAt);
        await BroadcastClosedAsync(canvas, ct);
        await BroadcastPagesAsync(canvas, ct);
        return CanvasResult.Ok(canvas);
    }

    private async Task<CanvasResult<CanvasOutcome>> CreateAsync(
        string sessionId,
        string kind,
        string title,
        JsonNode? state,
        CancellationToken ct)
    {
        var change = CanvasOps.Open(kind, state);
        if (!change.IsSuccess)
            return CanvasResult.Fail<CanvasOutcome>(change.Error);

        var now = DateTime.UtcNow.ToString("O");
        var canvas = new Canvas
        {
            Id = "cv_" + Ulid.NewUlid(),
            SessionId = sessionId,
            UserId = userContext.UserId,
            Kind = kind,
            Title = title,
            StateJson = change.Value.StateJson,
            Version = 1,
            AgentSeenVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var revision = new CanvasRevision
        {
            CanvasId = canvas.Id,
            Version = 1,
            Actor = CanvasRevision.AgentActor,
            OpsJson = CanvasOps.ToJson(change.Value.Ops),
            CreatedAt = now,
        };

        if (!await canvasRepository.InsertAsync(canvas, revision))
            return CanvasResult.Fail<CanvasOutcome>(CanvasErrorKind.NotFound, $"No session {sessionId}.");

        await BroadcastUpdatedAsync(canvas, CanvasActor.Agent, change.Value.Summary, ct);
        await BroadcastPagesAsync(canvas, ct);
        await BroadcastFocusedAsync(canvas, ct);
        return CanvasResult.Ok(new CanvasOutcome(canvas, change.Value.Summary, Created: true));
    }

    /// <summary>Writes the next version, or returns <c>null</c> if another writer got there first.</summary>
    private async Task<Canvas?> TryWriteAsync(Canvas current, CanvasChange change, CanvasActor actor, int agentSeenVersion)
    {
        var now = DateTime.UtcNow.ToString("O");
        var next = new Canvas
        {
            Id = current.Id,
            SessionId = current.SessionId,
            UserId = current.UserId,
            Kind = current.Kind,
            Title = current.Title,
            StateJson = change.StateJson,
            Version = current.Version + 1,
            AgentSeenVersion = Math.Max(current.AgentSeenVersion, agentSeenVersion),
            CreatedAt = current.CreatedAt,
            UpdatedAt = now,
            ClosedAt = current.ClosedAt,
        };
        var revision = new CanvasRevision
        {
            CanvasId = current.Id,
            Version = next.Version,
            Actor = actor.ToRevisionActor(),
            OpsJson = CanvasOps.ToJson(change.Ops),
            CreatedAt = now,
        };

        return await canvasRepository.TryUpdateAsync(next, current.Version, revision) ? next : null;
    }

    private Task BroadcastUpdatedAsync(Canvas canvas, CanvasActor actor, string summary, CancellationToken ct)
    {
        using var state = JsonDocument.Parse(canvas.StateJson);
        var domainEvent = new CanvasUpdated
        {
            Payload = new CanvasUpdatedPayload
            {
                SessionId = canvas.SessionId,
                CanvasId = canvas.Id,
                Kind = canvas.Kind,
                Title = canvas.Title,
                Version = canvas.Version,
                Actor = actor.ToRevisionActor(),
                State = state.RootElement.Clone(),
                Summary = summary,
            },
        };

        return eventBroadcaster.BroadcastAsync(
            Topic(canvas.SessionId),
            "canvas.updated",
            JsonSerializer.SerializeToElement(domainEvent.Payload, ApplicationJsonContext.Default.CanvasUpdatedPayload),
            domainEvent,
            canvas.UserId,
            ct);
    }

    /// <summary>A page was shown, reopened or closed: the session's row in the list hears its pages now.</summary>
    private async Task BroadcastPagesAsync(Canvas canvas, CancellationToken ct)
    {
        if (canvas.Kind != CanvasKinds.Page)
            return;

        var pages = (await canvasRepository.SummarizePagesAsync(canvas.SessionId)).GetValueOrDefault(canvas.SessionId);
        var payload = JsonSerializer.SerializeToElement(
            new SessionPagesChanged(canvas.SessionId, pages?.Count ?? 0, pages?.LastShownAt),
            ApplicationJsonContext.Default.SessionPagesChanged);
        await eventBroadcaster.BroadcastAsync("sessions", PagesChangedEvent, payload, canvas.UserId, ct);
    }

    private Task BroadcastFocusedAsync(Canvas canvas, CancellationToken ct)
    {
        var payload = RefPayload(canvas);
        return BroadcastRefAsync(canvas.UserId, "canvas.focused", payload, new CanvasFocused { Payload = payload }, ct);
    }

    private Task BroadcastClosedAsync(Canvas canvas, CancellationToken ct)
    {
        var payload = RefPayload(canvas);
        return BroadcastRefAsync(canvas.UserId, "canvas.closed", payload, new CanvasClosed { Payload = payload }, ct);
    }

    private Task BroadcastRefAsync(string userId, string type, CanvasRefPayload payload, DomainEvent domainEvent, CancellationToken ct)
        => eventBroadcaster.BroadcastAsync(
            Topic(payload.SessionId),
            type,
            JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.CanvasRefPayload),
            domainEvent,
            userId,
            ct);

    private static CanvasRefPayload RefPayload(Canvas canvas) => new() { SessionId = canvas.SessionId, CanvasId = canvas.Id };

    private static CanvasError NotFound(string canvasId)
        => new(CanvasErrorKind.NotFound, $"No canvas {canvasId} in this session. Call fleet_canvas_list to see the open canvases.");

    private static string Topic(string sessionId) => $"session:{sessionId}";
}
