namespace NuCode.Lsp;

/// <summary>
/// Represents a pull-diagnostics response (textDocument/diagnostic).
/// Kind is "full" (Items contains all current diagnostics) or "unchanged"
/// (Items is empty; client should keep cached diagnostics).
/// </summary>
internal sealed record LspDiagnosticReport
{
    public required string Kind { get; init; }
    public required IReadOnlyList<LspDiagnostic> Items { get; init; }
    public string? ResultId { get; init; }
}
