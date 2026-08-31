namespace WeaveFleet.Domain.Events;

/// <summary>
/// Raised when files have been changed in a Fleet session.
/// </summary>
public sealed record FilesChanged : DomainEvent
{
    /// <summary>
    /// Gets the strongly typed payload for the files-changed event.
    /// </summary>
    public required FilesChangedPayload Payload { get; init; }
}

/// <summary>
/// Payload describing files that have been changed in a session.
/// </summary>
public sealed record FilesChangedPayload
{
    /// <summary>
    /// Gets the Fleet session identifier.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Gets the list of file changes.
    /// </summary>
    public IReadOnlyList<FileChangeEntry> Files { get; init; } = [];
}

/// <summary>
/// Describes a single file change.
/// </summary>
public sealed record FileChangeEntry
{
    /// <summary>
    /// Gets the file path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the type of change applied to the file.
    /// </summary>
    public required string ChangeType { get; init; }
}
