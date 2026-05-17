namespace NuCode.Lsp;

/// <summary>
/// Represents a source location (file path + position range).
/// </summary>
public sealed record LspLocation
{
    /// <summary>Absolute path to the file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Start line (0-indexed).</summary>
    public required int StartLine { get; init; }

    /// <summary>Start character (0-indexed).</summary>
    public required int StartCharacter { get; init; }

    /// <summary>End line (0-indexed).</summary>
    public required int EndLine { get; init; }

    /// <summary>End character (0-indexed).</summary>
    public required int EndCharacter { get; init; }

    /// <summary>Formats as file:line:col for LLM consumption (1-indexed).</summary>
    public override string ToString() =>
        $"{FilePath}:{StartLine + 1}:{StartCharacter + 1}";
}
