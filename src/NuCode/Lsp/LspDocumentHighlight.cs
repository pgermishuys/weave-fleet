namespace NuCode.Lsp;

/// <summary>The kind of a document highlight.</summary>
public enum LspDocumentHighlightKind
{
    /// <summary>A textual occurrence.</summary>
    Text = 1,

    /// <summary>Read-access of a symbol.</summary>
    Read = 2,

    /// <summary>Write-access of a symbol.</summary>
    Write = 3,
}

/// <summary>A document highlight returned by the LSP server.</summary>
public sealed record LspDocumentHighlight
{
    /// <summary>The start line of the highlight range.</summary>
    public required int StartLine { get; init; }

    /// <summary>The start character of the highlight range.</summary>
    public required int StartCharacter { get; init; }

    /// <summary>The end line of the highlight range.</summary>
    public required int EndLine { get; init; }

    /// <summary>The end character of the highlight range.</summary>
    public required int EndCharacter { get; init; }

    /// <summary>The highlight kind (Text, Read, or Write).</summary>
    public required LspDocumentHighlightKind Kind { get; init; }
}
