namespace NuCode.Lsp;

/// <summary>Semantic tokens returned by the LSP server (encoded integer array per LSP spec).</summary>
public sealed record LspSemanticTokens
{
    /// <summary>The encoded token data as a flat integer array (groups of 5: deltaLine, deltaStart, length, tokenType, tokenModifiers).</summary>
    public required IReadOnlyList<int> Data { get; init; }

    /// <summary>An optional result ID for delta requests.</summary>
    public string? ResultId { get; init; }
}

/// <summary>Legend describing the token types and modifiers used by the server.</summary>
public sealed record LspSemanticTokensLegend
{
    /// <summary>The token type names (indices correspond to tokenType values in the data array).</summary>
    public required IReadOnlyList<string> TokenTypes { get; init; }

    /// <summary>The token modifier names (bit positions correspond to tokenModifiers values in the data array).</summary>
    public required IReadOnlyList<string> TokenModifiers { get; init; }
}
