namespace NuCode.Lsp;

/// <summary>A progress value reported by the LSP server via $/progress notifications.</summary>
public sealed record LspProgressValue
{
    /// <summary>The progress token identifying the operation.</summary>
    public required string Token { get; init; }

    /// <summary>The kind of progress report ("begin", "report", or "end").</summary>
    public required string Kind { get; init; }

    /// <summary>The title of the operation (typically set in "begin").</summary>
    public string? Title { get; init; }

    /// <summary>An optional message describing current progress.</summary>
    public string? Message { get; init; }

    /// <summary>An optional percentage (0-100) of completion.</summary>
    public int? Percentage { get; init; }
}
