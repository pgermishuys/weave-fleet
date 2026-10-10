using System.Diagnostics.CodeAnalysis;

namespace WeaveFleet.Application.Runtimes;

/// <summary>
/// Fleet's Bun manifest, <c>bun.json</c>: the Bun Fleet recommends, the oldest version that's safe to run, a note, and
/// each platform's archive and sha256. It's kept in weave-fleet as <c>bun/bun.json</c>, built into Fleet as the
/// fallback (<see cref="BunRelease.Pinned"/>), and published to Fleet's release repository, where Fleet reads it on
/// its update schedule. Parsed strictly: anything unexpected is refused, never guessed at.
/// </summary>
/// <example><code>
/// { "schema": 1, "version": "1.4.3", "oldestSafe": "1.4.3", "note": "Bun 1.4.3 fixes …",
///   "assets": { "linux-x64": { "name": "bun-linux-x64-baseline.zip", "sha256": "…64 hex…" }, … } }
/// </code></example>
public static class BunManifest
{
    /// <summary>The only <c>schema</c> this Fleet reads.</summary>
    public const int Schema = 1;

    /// <summary>The most bytes a manifest may have; a bigger one is refused unread.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>
    /// Every platform Fleet ships, with the one Bun asset name it accepts for it (Bun's own release naming; the x64
    /// builds are the <c>-baseline</c> ones). A manifest must name exactly these.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssetNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["linux-x64"] = "bun-linux-x64-baseline.zip",
        ["linux-arm64"] = "bun-linux-aarch64.zip",
        ["osx-x64"] = "bun-darwin-x64-baseline.zip",
        ["osx-arm64"] = "bun-darwin-aarch64.zip",
        ["win-x64"] = "bun-windows-x64-baseline.zip",
        ["win-arm64"] = "bun-windows-aarch64.zip",
    };

    /// <summary>
    /// Reads a manifest. On success <paramref name="release"/> is the release it describes; otherwise
    /// <paramref name="error"/> says, in a sentence, the first thing wrong with it.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> json, [NotNullWhen(true)] out BunRelease? release, [NotNullWhen(false)] out string? error) =>
        throw new NotImplementedException();
}
