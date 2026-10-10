using WeaveFleet.Application.Runtimes;

namespace WeaveFleet.Infrastructure.Runtimes;

/// <summary>What running <c>{bun} --version</c> said.</summary>
/// <param name="Version">The version it printed, when it ran, exited 0 and printed exactly one version.</param>
/// <param name="Error">Why there's no version, in a sentence fit to show the user; <see langword="null"/> with one.</param>
internal sealed record BunProbeResult(BunVersion? Version, string? Error);

/// <summary>
/// Runs <c>{bun} --version</c> to learn a Bun's version: directly, with no shell, a clean environment and a time
/// limit, reading at most a little output, and parsing it strictly.
/// </summary>
internal static class BunVersionProbe
{
    /// <summary>How long <c>--version</c> may take before Fleet gives up on it.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs <paramref name="executablePath"/> <c>--version</c>. Never throws for a Bun that won't run.</summary>
    public static Task<BunProbeResult> RunAsync(string executablePath, TimeSpan timeout, CancellationToken ct) =>
        throw new NotImplementedException();
}
