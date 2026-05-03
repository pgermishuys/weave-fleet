namespace NuCode.Lsp;

/// <summary>A folding range returned by the LSP server.</summary>
public sealed record LspFoldingRange
{
    /// <summary>The start line of the folding range (zero-based).</summary>
    public required int StartLine { get; init; }

    /// <summary>The start character of the folding range (optional).</summary>
    public int? StartCharacter { get; init; }

    /// <summary>The end line of the folding range (zero-based).</summary>
    public required int EndLine { get; init; }

    /// <summary>The end character of the folding range (optional).</summary>
    public int? EndCharacter { get; init; }

    /// <summary>The kind of folding range ("comment", "imports", "region", or null).</summary>
    public string? Kind { get; init; }
}
