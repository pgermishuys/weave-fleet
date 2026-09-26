using System.Collections.Concurrent;
using System.ComponentModel;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>Where Fleet finds the <c>opencode</c> executable. Spawning and the availability check use the same lookup.</summary>
/// <remarks>
/// OpenCode 2 installs as <c>opencode</c> too, over OpenCode 1 at the same path, and has a different server API.
/// This harness runs OpenCode 1 only, so it turns a 2.x install away rather than starting it.
/// </remarks>
internal static class OpenCodeExecutable
{
    public const string Command = "opencode";

    /// <summary>The first OpenCode 2 release. This harness needs a version older than this.</summary>
    public const string FirstOpenCode2Version = "2.0.0";

    // Version per executable, keyed by its real path and write time: an install or update changes the key.
    private static readonly ConcurrentDictionary<(string Path, DateTime Written), string?> Versions = new();

    /// <summary>The executable's path, or <c>"opencode"</c> when it isn't found.</summary>
    public static string Resolve() => ExecutableResolver.Resolve(Command, InstallDirectories());

    /// <summary>
    /// Where the install script (<c>curl -fsSL https://opencode.ai/install | bash</c>) puts it:
    /// <c>$OPENCODE_INSTALL_DIR</c> or <c>$XDG_BIN_DIR</c> when set, else <c>~/.opencode/bin</c>.
    /// </summary>
    public static IEnumerable<string> InstallDirectories()
    {
        foreach (var variable in new[] { "OPENCODE_INSTALL_DIR", "XDG_BIN_DIR" })
        {
            var directory = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(directory)) yield return directory;
        }

        var home = ExecutableResolver.HomeDirectory();
        if (home is not null) yield return Path.Combine(home, ".opencode", "bin");
    }

    public static bool IsOpenCode2(string version) => !HarnessVersion.IsOlder(version, FirstOpenCode2Version);

    /// <summary>
    /// Why this harness won't use <paramref name="executablePath"/>, and what installing OpenCode 1 does to the OpenCode 2
    /// there: both installers write <c>~/.opencode/bin/opencode</c>, and V2's <c>opencode2</c> runs whatever is in it.
    /// </summary>
    public static string OpenCode2Reason(string version, string? executablePath) =>
        $"{executablePath ?? Command} is OpenCode 2 ({version}), and the OpenCode harness needs OpenCode 1. " +
        "Installing OpenCode 1 replaces an OpenCode 2 in ~/.opencode/bin, where both install. " +
        "To keep OpenCode 2 as well, install it again in its own folder afterwards (Settings → Harnesses → OpenCode 2).";

    /// <summary>A ready OpenCode 2 install is not working for this harness; anything else is returned as it is.</summary>
    public static HarnessAvailability RejectOpenCode2(HarnessAvailability availability) =>
        availability is { Available: true, Version: { } version } && IsOpenCode2(version)
            ? HarnessAvailability.NotWorking(OpenCode2Reason(version, availability.ExecutablePath), version, availability.ExecutablePath)
            : availability;

    /// <summary>
    /// Throws when <paramref name="executablePath"/> is OpenCode 2, so Fleet never starts it as OpenCode 1: its
    /// server would come up on the user's real data, migrate their database, and never answer Fleet's API.
    /// Runs <c>--version</c> once per binary. A path that isn't a file, or a probe that fails, is left to the
    /// start itself to report.
    /// </summary>
    public static async Task RejectOpenCode2Async(string executablePath, CancellationToken ct)
    {
        var file = new FileInfo(executablePath);
        if (!file.Exists)
            return;
        if (file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
            file = target;

        var key = (file.FullName, file.LastWriteTimeUtc);
        if (!Versions.TryGetValue(key, out var version))
        {
            try
            {
                var result = await HarnessProbe.RunAsync(file.FullName, ["--version"], ct).ConfigureAwait(false);
                version = result.ExitCode == 0 ? HarnessProbe.ParseVersion(result.StandardOutput) : null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                return;
            }
            Versions[key] = version;
        }

        if (version is not null && IsOpenCode2(version))
            throw new InvalidOperationException(OpenCode2Reason(version, executablePath));
    }
}
