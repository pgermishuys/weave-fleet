namespace NuCode.Lsp;

/// <summary>The kind of an inlay hint.</summary>
public enum LspInlayHintKind
{
    /// <summary>A type annotation hint.</summary>
    Type = 1,

    /// <summary>A parameter name hint.</summary>
    Parameter = 2,
}

/// <summary>An inlay hint returned by the LSP server.</summary>
public sealed record LspInlayHint
{
    /// <summary>The line of the hint position.</summary>
    public required int Line { get; init; }

    /// <summary>The character offset of the hint position.</summary>
    public required int Character { get; init; }

    /// <summary>The label text of the hint.</summary>
    public required string Label { get; init; }

    /// <summary>The kind of hint (type or parameter).</summary>
    public LspInlayHintKind? Kind { get; init; }

    /// <summary>Whether to add padding before the hint.</summary>
    public bool PaddingLeft { get; init; }

    /// <summary>Whether to add padding after the hint.</summary>
    public bool PaddingRight { get; init; }
}
