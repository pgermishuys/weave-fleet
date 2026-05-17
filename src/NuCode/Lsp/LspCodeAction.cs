namespace NuCode.Lsp;

/// <summary>A code action returned by textDocument/codeAction.</summary>
public sealed record LspCodeAction
{
    public required string Title { get; init; }
    public string? Kind { get; init; }
    public IReadOnlyList<LspDiagnostic>? Diagnostics { get; init; }
    public bool IsPreferred { get; init; }
}
