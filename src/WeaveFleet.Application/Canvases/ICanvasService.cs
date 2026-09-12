using System.Text.Json.Nodes;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>An open canvas, and whether the user removed something the agent hasn't read yet.</summary>
public sealed record CanvasListItem(Canvas Canvas, bool ChangedByUser);

/// <summary>An accepted open or change: the canvas as stored, and a short summary for tool cards.</summary>
public sealed record CanvasOutcome(Canvas Canvas, string Summary, bool Created);

/// <summary>
/// Session canvases shared by the user and the agent. Every call runs as the current user and only
/// sees that user's sessions. Accepted changes and focus requests are pushed on <c>session:{id}</c>
/// as <c>canvas.updated</c>, <c>canvas.closed</c> and <c>canvas.focused</c>.
/// </summary>
public interface ICanvasService
{
    /// <summary>Lists the session's open canvases, oldest first.</summary>
    Task<IReadOnlyList<CanvasListItem>> ListAsync(string sessionId, CancellationToken ct = default);

    Task<Canvas?> GetAsync(string sessionId, string canvasId, CancellationToken ct = default);

    /// <summary>
    /// Opens a canvas for the agent. A canvas with the same title, open or closed, is reopened and
    /// changed to <paramref name="state"/> instead of creating another; boxes it keeps stay where the user put them.
    /// </summary>
    Task<CanvasResult<CanvasOutcome>> OpenAsync(string sessionId, string kind, string title, JsonNode? state, CancellationToken ct = default);

    /// <summary>
    /// Reads a canvas for the agent: the whole canvas as text when <paramref name="full"/> is set,
    /// otherwise only what the user removed since the agent last looked. Either way the agent has now seen it.
    /// </summary>
    Task<CanvasResult<string>> ReadAsync(string sessionId, string canvasId, bool full, CancellationToken ct = default);

    /// <summary>
    /// Applies a batch of ops from <paramref name="actor"/>. An agent batch that touches something the
    /// user removed since the agent last looked is refused.
    /// </summary>
    Task<CanvasResult<CanvasOutcome>> ApplyAsync(string sessionId, string canvasId, CanvasActor actor, JsonNode? ops, CancellationToken ct = default);

    /// <summary>Brings a canvas to the front, reopening it if it was closed.</summary>
    Task<CanvasResult<Canvas>> FocusAsync(string sessionId, string canvasId, CancellationToken ct = default);

    Task<CanvasResult<Canvas>> CloseAsync(string sessionId, string canvasId, CancellationToken ct = default);
}
