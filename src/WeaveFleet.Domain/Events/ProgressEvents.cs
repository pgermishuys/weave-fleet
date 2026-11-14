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
/// Raised when the agent finished writing files with a tool: an edit, a new file, a patch.
/// </summary>
public sealed record FilesWritten : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the files-written event.
    /// </summary>
    public required FilesWrittenPayload Payload { get; init; }
}

/// <summary>
/// Payload naming the files one tool call wrote.
/// </summary>
public sealed record FilesWrittenPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the message the tool call belongs to, when the harness says.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Gets the absolute paths the call wrote.
    /// </summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Gets what the call did to each file, when the harness says. Fleet may only read a file well after the call
    /// wrote it, by which time a later call may have changed it again; a change says what this call left.
    /// </summary>
    public IReadOnlyList<FileChange> Changes { get; init; } = [];
}

/// <summary>
/// What one tool call did to one file: wrote <see cref="Content"/> as the whole file, or replaced
/// <see cref="OldString"/> with <see cref="NewString"/> (once, unless <see cref="ReplaceAll"/>).
/// </summary>
public sealed record FileChange
{
    /// <summary>Gets the absolute path of the file, as in <see cref="FilesWrittenPayload.Paths"/>.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Gets the whole file the call wrote, for a call that writes the whole file.</summary>
    public string? Content { get; init; }

    /// <summary>Gets the exact text the call replaced, for an edit.</summary>
    public string? OldString { get; init; }

    /// <summary>Gets the text the call put in its place, for an edit.</summary>
    public string? NewString { get; init; }

    /// <summary>Gets whether the edit replaced every occurrence of <see cref="OldString"/>, not exactly one.</summary>
    public bool ReplaceAll { get; init; }
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
