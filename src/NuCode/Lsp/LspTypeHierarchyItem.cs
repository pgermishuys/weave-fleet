namespace NuCode.Lsp;

/// <summary>
/// Represents an item in a type hierarchy.
/// </summary>
public sealed record LspTypeHierarchyItem
{
    /// <summary>Name of the type.</summary>
    public required string Name { get; init; }

    /// <summary>Kind of the symbol (e.g., Class, Interface).</summary>
    public required string Kind { get; init; }

    /// <summary>Location of the type.</summary>
    public required LspLocation Location { get; init; }

    /// <summary>Detail string (e.g., containing namespace or module).</summary>
    public string? Detail { get; init; }
}
