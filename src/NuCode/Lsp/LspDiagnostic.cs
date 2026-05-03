namespace NuCode.Lsp;

/// <summary>Severity of an LSP diagnostic.</summary>
public enum LspDiagnosticSeverity
{
    /// <summary>An error.</summary>
    Error = 1,

    /// <summary>A warning.</summary>
    Warning = 2,

    /// <summary>An informational message.</summary>
    Information = 3,

    /// <summary>A hint.</summary>
    Hint = 4,
}

/// <summary>
/// A diagnostic (error, warning, hint, etc.) reported by an LSP server.
/// </summary>
public sealed record LspDiagnostic
{
    /// <summary>Absolute path to the file this diagnostic applies to.</summary>
    public required string FilePath { get; init; }

    /// <summary>Start line (0-indexed).</summary>
    public required int StartLine { get; init; }

    /// <summary>Start character (0-indexed).</summary>
    public required int StartCharacter { get; init; }

    /// <summary>End line (0-indexed).</summary>
    public required int EndLine { get; init; }

    /// <summary>End character (0-indexed).</summary>
    public required int EndCharacter { get; init; }

    /// <summary>Diagnostic severity.</summary>
    public required LspDiagnosticSeverity Severity { get; init; }

    /// <summary>Human-readable diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Source that produced the diagnostic (e.g. "csharp", "typescript").</summary>
    public string? Source { get; init; }

    /// <summary>Diagnostic code (may be a number or string).</summary>
    public string? Code { get; init; }
}
