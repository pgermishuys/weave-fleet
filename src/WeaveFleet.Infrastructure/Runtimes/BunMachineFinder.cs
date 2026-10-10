using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>
/// Finds the Buns on the machine Fleet can offer the user, in every build: <c>bun</c> on <c>PATH</c>, then
/// <c>~/.bun/bin</c>, <c>/opt/homebrew/bin</c> and <c>/usr/local/bin</c>; on Windows <c>bun.exe</c> on <c>PATH</c>
/// and in <c>%USERPROFILE%\.bun\bin</c>. Each file once, however many links lead to it.
/// </summary>
internal sealed class BunMachineFinder
{
    /// <summary>Test seam: the user's home folder.</summary>
    public required string Home { get; init; }

    /// <summary>Test seam: the <c>PATH</c> value to search.</summary>
    public string? PathEnv { get; init; } = Environment.GetEnvironmentVariable("PATH");

    /// <summary>Test seam: the fixed folders searched after <c>~/.bun/bin</c>, except on Windows.</summary>
    internal IReadOnlyList<string> SystemDirectories { get; init; } = ["/opt/homebrew/bin", "/usr/local/bin"];

    /// <summary>Test seam: whether to look the way Windows does.</summary>
    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Test seam: learns a Bun's version.</summary>
    public Func<string, CancellationToken, Task<BunProbeResult>> Probe { get; init; } =
        (path, ct) => BunVersionProbe.RunAsync(path, BunVersionProbe.DefaultTimeout, ct);

    /// <summary>Every Bun found, in search order, each with its version and status.</summary>
    public Task<IReadOnlyList<BunCandidate>> FindAsync(CancellationToken ct) => throw new NotImplementedException();

    /// <summary>Checks one path the user typed. A relative path, or one that doesn't exist, is not working.</summary>
    public Task<BunCandidate> CheckAsync(string path, CancellationToken ct) => throw new NotImplementedException();
}
