using System.Text.Json;

namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when a canvas is opened, reopened or changed. The client upserts the canvas tab from it.
/// </summary>
public sealed record CanvasUpdated : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the canvas-updated event.
    /// </summary>
    public required CanvasUpdatedPayload Payload { get; init; }
}

/// <summary>
/// Raised when a canvas is closed.
/// </summary>
public sealed record CanvasClosed : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the canvas-closed event.
    /// </summary>
    public required CanvasRefPayload Payload { get; init; }
}

/// <summary>
/// Raised when a canvas should be brought to the front of the session's right panel.
/// </summary>
public sealed record CanvasFocused : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the canvas-focused event.
    /// </summary>
    public required CanvasRefPayload Payload { get; init; }
}

/// <summary>
/// Payload describing a canvas after a change.
/// </summary>
public sealed record CanvasUpdatedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the canvas identifier.
    /// </summary>
    public required string CanvasId { get; init; }

    /// <summary>
    /// Gets the canvas kind, e.g. <c>diagram</c> or <c>sequence</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Gets the canvas title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets the canvas version after the change.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Gets who made the change: <c>agent</c> or <c>user</c>.
    /// </summary>
    public required string Actor { get; init; }

    /// <summary>
    /// Gets the full canvas state, including the positions the user has set.
    /// </summary>
    public required JsonElement State { get; init; }

    /// <summary>
    /// Gets a short summary of the change, e.g. "+2 boxes, −1 edge".
    /// </summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Payload naming one canvas in a session.
/// </summary>
public sealed record CanvasRefPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the canvas identifier.
    /// </summary>
    public required string CanvasId { get; init; }
}
