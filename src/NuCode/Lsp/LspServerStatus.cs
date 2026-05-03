namespace NuCode.Lsp;

/// <summary>Status of a managed LSP server connection.</summary>
public sealed record LspServerStatus
{
    /// <summary>The configured name of the LSP server.</summary>
    public required string ServerName { get; init; }

    /// <summary>Whether the server process is currently running.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>Whether the server has faulted (process exited unexpectedly).</summary>
    public required bool IsFaulted { get; init; }

    /// <summary>The number of times the server has been restarted.</summary>
    public required int RestartCount { get; init; }

    /// <summary>The maximum number of automatic restarts allowed.</summary>
    public required int MaxRestarts { get; init; }
}
