namespace NuCode.Lsp;

/// <summary>A text edit returned by textDocument/formatting or workspace edits.</summary>
public sealed record LspTextEdit
{
    public required int StartLine { get; init; }
    public required int StartCharacter { get; init; }
    public required int EndLine { get; init; }
    public required int EndCharacter { get; init; }
    public required string NewText { get; init; }
}
