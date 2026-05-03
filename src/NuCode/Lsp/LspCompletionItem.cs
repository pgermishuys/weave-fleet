namespace NuCode.Lsp;

/// <summary>The kind of a completion item.</summary>
public enum LspCompletionItemKind
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

/// <summary>A single completion item returned by the LSP server.</summary>
public sealed record LspCompletionItem
{
    /// <summary>The label shown in the completion list.</summary>
    public required string Label { get; init; }

    /// <summary>The kind of completion item.</summary>
    public required LspCompletionItemKind Kind { get; init; }

    /// <summary>Additional detail shown alongside the label (e.g. type signature).</summary>
    public string? Detail { get; init; }

    /// <summary>The text to insert when the item is accepted. Defaults to Label when null.</summary>
    public string? InsertText { get; init; }

    /// <summary>Used for filtering the completion list by typed text.</summary>
    public string? FilterText { get; init; }

    /// <summary>Used for sorting the completion list.</summary>
    public string? SortText { get; init; }
}
