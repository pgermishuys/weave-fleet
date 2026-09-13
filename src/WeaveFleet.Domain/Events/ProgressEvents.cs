namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when the agent's todo list changes. Each item is the agent's own wording.
/// </summary>
public sealed record TodosReported : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the todos-reported event.
    /// </summary>
    public required TodosReportedPayload Payload { get; init; }
}

/// <summary>
/// Payload carrying the whole todo list as the agent last wrote it.
/// </summary>
public sealed record TodosReportedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the todo list, in the agent's order. An empty list means the agent cleared it.
    /// </summary>
    public IReadOnlyList<TodoEntry> Items { get; init; } = [];
}

/// <summary>
/// One item on the agent's todo list.
/// </summary>
public sealed record TodoEntry
{
    /// <summary>
    /// Gets what the item says.
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// Gets the item's status, one of <see cref="TodoStatuses"/>.
    /// </summary>
    public string Status { get; init; } = TodoStatuses.Pending;

    /// <summary>
    /// Gets the priority the agent gave the item (<c>high</c>, <c>medium</c> or <c>low</c>), when it gave one.
    /// </summary>
    public string? Priority { get; init; }
}

/// <summary>
/// The statuses a todo item can have.
/// </summary>
public static class TodoStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    /// <summary>Returns the status if it's one of the known values, otherwise <see cref="Pending"/>.</summary>
    public static string Normalize(string? status) => status switch
    {
        InProgress or Completed or Cancelled => status,
        _ => Pending,
    };
}
