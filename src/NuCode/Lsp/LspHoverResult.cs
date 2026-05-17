namespace NuCode.Lsp;

/// <summary>
/// Result of a hover operation.
/// </summary>
public sealed record LspHoverResult
{
    /// <summary>The hover content (typically markdown).</summary>
    public required string Content { get; init; }

    /// <summary>Optional location range of the hovered symbol.</summary>
    public LspLocation? Range { get; init; }
}
