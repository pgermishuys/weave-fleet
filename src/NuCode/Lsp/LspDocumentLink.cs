namespace NuCode.Lsp;

/// <summary>A document link returned by the LSP server.</summary>
public sealed record LspDocumentLink
{
    /// <summary>The start line of the link range.</summary>
    public required int StartLine { get; init; }

    /// <summary>The start character of the link range.</summary>
    public required int StartCharacter { get; init; }

    /// <summary>The end line of the link range.</summary>
    public required int EndLine { get; init; }

    /// <summary>The end character of the link range.</summary>
    public required int EndCharacter { get; init; }

    /// <summary>The target URI of the link (null if unresolved).</summary>
    public string? Target { get; init; }

    /// <summary>An optional tooltip shown when hovering over the link.</summary>
    public string? Tooltip { get; init; }
}
