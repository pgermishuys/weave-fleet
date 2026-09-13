namespace WeaveFleet.Application.Terminals;

/// <summary>
/// What to start in a new pseudoterminal. <see cref="Environment"/> is the child's whole environment:
/// variables in Fleet's own environment that aren't listed here are removed.
/// </summary>
public sealed record PtySpawnOptions(
    string Shell,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    int Cols,
    int Rows,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>How a pseudoterminal's process ended. <see cref="ExitCode"/> is null when Fleet killed it.</summary>
public sealed record PtyExit(int? ExitCode, bool Killed);

/// <summary>Starts processes attached to a pseudoterminal: a Unix PTY, or ConPTY on Windows.</summary>
public interface IPtyFactory
{
    Task<IPtyProcess> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default);
}

/// <summary>A running process on a pseudoterminal.</summary>
public interface IPtyProcess : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>Completes once the process has ended.</summary>
    Task<PtyExit> Exited { get; }

    /// <summary>
    /// Reads the next chunk of output, split wherever the OS splits it. Returns 0 once the terminal has
    /// closed, and never throws for a closed terminal.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    /// <summary>Writes input. Writing after the process has ended does nothing.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    void Resize(int cols, int rows);

    /// <summary>Ends the process and everything in its terminal session. Safe to call more than once.</summary>
    void Kill();
}
