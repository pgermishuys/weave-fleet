namespace NuCode.Lsp;

/// <summary>A workspace edit returned by textDocument/rename.</summary>
public sealed record LspWorkspaceEdit
{
    public required IReadOnlyDictionary<string, IReadOnlyList<LspTextEdit>> Changes { get; init; }
}
