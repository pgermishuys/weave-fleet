using System.Text.Json;
using System.Text.Json.Nodes;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Canvases;

public sealed class CanvasService(
    ICanvasRepository canvasRepository,
    IEventBroadcaster eventBroadcaster,
    IUserContext userContext) : ICanvasService
{
    // Writers only collide when the user and the agent change the same canvas at the same moment.
    private const int MaxWriteAttempts = 5;
    private const string ReopenedSummary = "reopened";

    public async Task<IReadOnlyList<CanvasListItem>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        var canvases = await canvasRepository.ListBySessionIdAsync(sessionId);
        var items = new List<CanvasListItem>(canvases.Count);
        foreach (var canvas in canvases)
            items.Add(new CanvasListItem(canvas, (await UserRemovalsSinceSeenAsync(canvas)).Count > 0));
        return items;
    }

    public Task<Canvas?> GetAsync(string sessionId, string canvasId, CancellationToken ct = default)
        => canvasRepository.GetByIdAsync(sessionId, canvasId);

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
                    await BroadcastUpdatedAsync(current, CanvasActor.Agent, ReopenedSummary, ct);
                await BroadcastFocusedAsync(current, ct);
                return CanvasResult.Ok(new CanvasOutcome(current, "no changes", Created: false));
            }

            var refusal = CanvasConflicts.FindRefusal(ops.Value, await UserRemovalsSinceSeenAsync(existing));
            if (refusal is not null)
                return CanvasResult.Fail<CanvasOutcome>(refusal);

            var change = CanvasOps.Apply(kind, CanvasActor.Agent, existing.StateJson, ops.Value);
            if (!change.IsSuccess)
                return CanvasResult.Fail<CanvasOutcome>(change.Error);

            // The agent sent the whole canvas, so after an open it has seen all of it.
            var updated = await TryWriteAsync(existing, change.Value, CanvasActor.Agent, agentSeenVersion: existing.Version + 1);
            if (updated is null)
                continue;

            if (wasClosed)
            {
                await canvasRepository.SetClosedAtAsync(sessionId, existing.Id, null);
                updated.ClosedAt = null;
            }

            await BroadcastUpdatedAsync(updated, CanvasActor.Agent, change.Value.Summary, ct);
            await BroadcastFocusedAsync(updated, ct);
            return CanvasResult.Ok(new CanvasOutcome(updated, change.Value.Summary, Created: false));
        }

        throw new InvalidOperationException($"Canvas \"{title}\" kept changing while it was being opened.");
    }

    public async Task<CanvasResult<string>> ReadAsync(string sessionId, string canvasId, bool full, CancellationToken ct = default)
    {
        var canvas = await canvasRepository.GetByIdAsync(sessionId, canvasId);
        if (canvas is null)
            return CanvasResult.Fail<string>(NotFound(canvasId));

        var text = full
            ? CanvasText.RenderFull(canvas)
            : CanvasText.RenderChangesSince(canvas.AgentSeenVersion, await UserRemovalsSinceSeenAsync(canvas));

        // Only the version that was read counts as seen, even if the user changed it again since.
        await canvasRepository.MarkAgentSeenAsync(sessionId, canvasId, canvas.Version);
        return CanvasResult.Ok(text);
    }

    public async Task<CanvasResult<CanvasOutcome>> ApplyAsync(
        string sessionId,
        string canvasId,
        CanvasActor actor,
        JsonNode? ops,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var canvas = await canvasRepository.GetByIdAsync(sessionId, canvasId);
            if (canvas is null)
                return CanvasResult.Fail<CanvasOutcome>(NotFound(canvasId));

            if (actor == CanvasActor.Agent && canvas.ClosedAt is not null)
            {
                return CanvasResult.Fail<CanvasOutcome>(
                    CanvasErrorKind.Refused,
                    $"Refused: the user closed {CanvasText.CanvasName(canvas)}. Call fleet_canvas_open with its title to reopen it.");
            }

            var parsed = CanvasOps.Parse(canvas.Kind, actor, ops);
            if (!parsed.IsSuccess)
                return CanvasResult.Fail<CanvasOutcome>(parsed.Error);

            IReadOnlyList<CanvasUserRemoval> removals = [];
            if (actor == CanvasActor.Agent)
            {
                removals = await UserRemovalsSinceSeenAsync(canvas);
                if (CanvasConflicts.FindRefusal(parsed.Value, removals) is { } refusal)
                    return CanvasResult.Fail<CanvasOutcome>(refusal);
            }

            var change = CanvasOps.Apply(canvas.Kind, actor, canvas.StateJson, parsed.Value);
            if (!change.IsSuccess)
                return CanvasResult.Fail<CanvasOutcome>(change.Error);

            // An agent write counts as seeing the canvas only when the user removed nothing it hasn't read.
            // Otherwise a patch that happens to miss a removed box would use up the refusal.
            var agentSeenVersion = actor == CanvasActor.Agent && removals.Count == 0
                ? canvas.Version + 1
                : canvas.AgentSeenVersion;

            var updated = await TryWriteAsync(canvas, change.Value, actor, agentSeenVersion);
            if (updated is null)
                continue;

            await BroadcastUpdatedAsync(updated, actor, change.Value.Summary, ct);
            return CanvasResult.Ok(new CanvasOutcome(updated, change.Value.Summary, Created: false));
        }

        throw new InvalidOperationException($"Canvas {canvasId} kept changing while a change was being applied.");
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

    private async Task<IReadOnlyList<CanvasUserRemoval>> UserRemovalsSinceSeenAsync(Canvas canvas)
        => CanvasConflicts.UserRemovals(await canvasRepository.ListRevisionsAsync(canvas.Id, canvas.AgentSeenVersion));

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
