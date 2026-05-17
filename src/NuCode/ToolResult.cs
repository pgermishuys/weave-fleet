namespace NuCode;

/// <summary>
/// Represents the result of a tool execution.
/// </summary>
/// <param name="Title">Human-readable title describing what the tool did.</param>
/// <param name="Output">The textual output of the tool execution.</param>
/// <param name="Metadata">Optional metadata key-value pairs about the execution.</param>
/// <param name="Attachments">Optional file attachments produced by the tool.</param>
public sealed record ToolResult(
    string Title,
    string Output,
    IReadOnlyDictionary<string, object>? Metadata = null,
    IReadOnlyList<ToolAttachment>? Attachments = null);
