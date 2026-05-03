namespace NuCode.Lsp;

/// <summary>
/// Represents an item in a call hierarchy.
/// </summary>
public sealed record LspCallHierarchyItem
{
    /// <summary>Name of the symbol.</summary>
    public required string Name { get; init; }

    /// <summary>Kind of the symbol (e.g., Method, Function).</summary>
    public required string Kind { get; init; }

    /// <summary>Location of the symbol.</summary>
    public required LspLocation Location { get; init; }

    /// <summary>Detail string (e.g., full signature or parent class).</summary>
    public string? Detail { get; init; }
}
