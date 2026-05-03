namespace NuCode.Sessions;

/// <summary>
/// Result of processing a single streaming iteration.
/// </summary>
public enum ProcessResult
{
    /// <summary>The agent loop should continue (e.g., tool calls were made).</summary>
    Continue,

    /// <summary>The agent loop should stop (finished, error, or blocked).</summary>
    Stop,

    /// <summary>Context overflow detected — compaction is needed before continuing.</summary>
    Compact,
}
