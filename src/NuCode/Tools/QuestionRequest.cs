namespace NuCode.Tools;

/// <summary>
/// Represents a pending question from the LLM to the user.
/// </summary>
public sealed record QuestionRequest
{
    /// <summary>Unique ID for this question request.</summary>
    public required string Id { get; init; }

    /// <summary>The session this question belongs to.</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>A header/title for the question.</summary>
    public required string Header { get; init; }

    /// <summary>The question text.</summary>
    public required string Question { get; init; }

    /// <summary>Suggested options for the user to choose from.</summary>
    public required IReadOnlyList<string> Options { get; init; }
}
