namespace NuCode.Lsp;

/// <summary>Represents a single file change event from the file system watcher.</summary>
internal sealed record LspFileChange
{
    public required string FilePath { get; init; }

    /// <summary>
    /// LSP FileChangeType: 1 = Created, 2 = Changed, 3 = Deleted.
    /// </summary>
    public required int ChangeType { get; init; }
}
