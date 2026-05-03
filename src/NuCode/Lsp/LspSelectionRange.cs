namespace NuCode.Lsp;

/// <summary>A selection range returned by the LSP server, with an optional parent forming a chain.</summary>
public sealed record LspSelectionRange
{
    /// <summary>The start line of the selection range.</summary>
    public required int StartLine { get; init; }

    /// <summary>The start character of the selection range.</summary>
    public required int StartCharacter { get; init; }

    /// <summary>The end line of the selection range.</summary>
    public required int EndLine { get; init; }

    /// <summary>The end character of the selection range.</summary>
    public required int EndCharacter { get; init; }

    /// <summary>The parent selection range containing this range (null for the outermost range).</summary>
    public LspSelectionRange? Parent { get; init; }
}
