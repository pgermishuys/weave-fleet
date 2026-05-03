namespace NuCode.Lsp;

/// <summary>A code lens returned by the LSP server.</summary>
public sealed record LspCodeLens
{
    /// <summary>The start line of the code lens range.</summary>
    public required int StartLine { get; init; }

    /// <summary>The start character of the code lens range.</summary>
    public required int StartCharacter { get; init; }

    /// <summary>The end line of the code lens range.</summary>
    public required int EndLine { get; init; }

    /// <summary>The end character of the code lens range.</summary>
    public required int EndCharacter { get; init; }

    /// <summary>The title of the associated command (null if unresolved).</summary>
    public string? CommandTitle { get; init; }

    /// <summary>The name/identifier of the associated command (null if unresolved).</summary>
    public string? CommandName { get; init; }

    /// <summary>Whether this code lens has been resolved (command details filled in).</summary>
    public required bool IsResolved { get; init; }
}
