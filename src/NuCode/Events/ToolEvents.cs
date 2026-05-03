namespace NuCode.Events;

/// <summary>
/// Tool execution events.
/// </summary>
public static class ToolEvents
{
    /// <summary>Properties for tool started events.</summary>
    public sealed record ToolStartedInfo(
        SessionId SessionId,
        MessageId MessageId,
        string ToolName,
        string? CallId);

    /// <summary>A tool invocation has started.</summary>
    public static readonly NuCodeEventDefinition<ToolStartedInfo> Started = new("tool.started");

    /// <summary>Properties for tool completed events.</summary>
    public sealed record ToolCompletedInfo(
        SessionId SessionId,
        MessageId MessageId,
        string ToolName,
        string? CallId,
        string? Title);

    /// <summary>A tool invocation has completed successfully.</summary>
    public static readonly NuCodeEventDefinition<ToolCompletedInfo> Completed = new("tool.completed");

    /// <summary>Properties for tool failed events.</summary>
    public sealed record ToolFailedInfo(
        SessionId SessionId,
        MessageId MessageId,
        string ToolName,
        string? CallId,
        string Error);

    /// <summary>A tool invocation has failed.</summary>
    public static readonly NuCodeEventDefinition<ToolFailedInfo> Failed = new("tool.failed");
}
