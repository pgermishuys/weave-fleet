namespace NuCode.Lsp;

/// <summary>
/// Represents a document or workspace symbol.
/// </summary>
public sealed record LspSymbol
{
    /// <summary>Symbol name.</summary>
    public required string Name { get; init; }

    /// <summary>Symbol kind (e.g., Class, Method, Function, Variable).</summary>
    public required string Kind { get; init; }

    /// <summary>Location of the symbol.</summary>
    public required LspLocation Location { get; init; }

    /// <summary>Name of the containing symbol (if any).</summary>
    public string? ContainerName { get; init; }
}
