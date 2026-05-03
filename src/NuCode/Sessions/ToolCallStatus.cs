namespace NuCode.Sessions;

/// <summary>
/// Represents the execution state of a tool call.
/// Transitions: Pending → Running → Completed | Error.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>Tool call is queued but not yet started.</summary>
    Pending,

    /// <summary>Tool execution is in progress.</summary>
    Running,

    /// <summary>Tool execution completed successfully.</summary>
    Completed,

    /// <summary>Tool execution failed.</summary>
    Error,
}
