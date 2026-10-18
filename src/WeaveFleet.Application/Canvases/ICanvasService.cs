using System.Text.Json.Nodes;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Canvases;

/// <summary>An open canvas.</summary>
public sealed record CanvasListItem(Canvas Canvas);

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
    /// Opens a canvas for Fleet's own tools (<c>fleet_page_show</c>, <c>fleet_browser_open</c>). A canvas with the same title, open or closed, is reopened and
    /// changed to <paramref name="state"/> instead of creating another.
    /// </summary>
    Task<CanvasResult<CanvasOutcome>> OpenAsync(string sessionId, string kind, string title, JsonNode? state, CancellationToken ct = default);

    /// <summary>Reads a canvas for the agent as compact text.</summary>
    Task<CanvasResult<string>> ReadAsync(string sessionId, string canvasId, CancellationToken ct = default);

    /// <summary>Brings a canvas to the front, reopening it if it was closed.</summary>
    Task<CanvasResult<Canvas>> FocusAsync(string sessionId, string canvasId, CancellationToken ct = default);

    Task<CanvasResult<Canvas>> CloseAsync(string sessionId, string canvasId, CancellationToken ct = default);

    /// <summary>
    /// Changes a page canvas for Fleet itself, such as the pick it waits for on the page: <paramref name="change"/> gets the
    /// page as it is and returns it changed, or null to leave it. Unlike <see cref="OpenAsync"/> it never reopens or focuses
    /// the canvas, and the page doesn't reload.
    /// </summary>
    Task<CanvasResult<Canvas>> UpdatePageAsync(string sessionId, string canvasId, Func<PageState, PageState?> change, CancellationToken ct = default);
}
