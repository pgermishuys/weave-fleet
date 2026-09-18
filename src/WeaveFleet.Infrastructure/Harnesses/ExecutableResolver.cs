using System.Runtime.InteropServices;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// Resolves an executable name to an absolute path on the current machine.
/// <para>
/// On Windows, <see cref="System.Diagnostics.ProcessStartInfo"/> with
/// <c>UseShellExecute = false</c> does not walk <c>PATHEXT</c>: passing <c>"opencode"</c> will
/// not match <c>opencode.cmd</c> or <c>opencode.bat</c>. This resolver walks <c>PATH</c>
/// explicitly and returns the first candidate that exists, so npm-installed CLIs
/// (<c>opencode</c>, <c>claude</c>, etc.) work without extra configuration.
/// </para>
/// <para>
/// After <c>PATH</c> it looks in the folders installers put CLIs in (<see cref="UserBinDirectories"/>).
/// An installer adds its folder to the user's shell profile, but Fleet read <c>PATH</c> when it
/// started (the desktop app from the login shell, the service from systemd), so without this a
/// harness installed while Fleet runs isn't found until Fleet restarts.
/// </para>
/// </summary>
internal static class ExecutableResolver
{
    /// <summary>
    /// Return an absolute path to <paramref name="name"/> on <c>PATH</c> or in a known install folder,
    /// or the original name when no candidate is found (callers should treat the latter as "not found").
    /// </summary>
    public static string Resolve(string name) => Resolve(name, []);

    /// <inheritdoc cref="Resolve(string)"/>
    /// <param name="name">The executable name, or a path (returned as is).</param>
    /// <param name="installDirectories">Folders this executable's own installer uses, searched after <c>PATH</c>.</param>
    public static string Resolve(string name, IEnumerable<string> installDirectories) =>
        TryResolve(name, installDirectories, out var path) ? path : name;

    /// <summary>
    /// Finds <paramref name="name"/> on <c>PATH</c>, then in <paramref name="installDirectories"/>, then in
    /// <see cref="UserBinDirectories"/>. A name that is already a path is found when the file exists.
    /// </summary>
    public static bool TryResolve(string name, IEnumerable<string> installDirectories, out string path) =>
        TryResolve(
            name,
            Environment.GetEnvironmentVariable("PATH"),
            installDirectories.Concat(UserBinDirectories()),
            out path);

    /// <summary>Test seam: resolves against the given <c>PATH</c> value and folders only.</summary>
    internal static bool TryResolve(string name, string? pathEnv, IEnumerable<string> directories, out string path)
    {
        path = name;
        if (string.IsNullOrWhiteSpace(name)) return false;

        // Absolute or relative path already — trust the caller.
        if (name.Contains('/') || name.Contains('\\')) return File.Exists(name);

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var pathDirectories = string.IsNullOrEmpty(pathEnv)
            ? []
            : pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsExtensions()
            : [""]; // Non-Windows: try the bare name.

        foreach (var dir in pathDirectories.Concat(directories))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(dir, name + ext); }
                catch (ArgumentException) { continue; } // skip malformed PATH entries
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Folders installers commonly put CLIs in: <c>~/.local/bin</c> (Claude Code's installer, pipx, XDG),
    /// <c>~/bin</c>, Homebrew on macOS, and on Windows winget's links, npm's global folder and Scoop's shims.
    /// </summary>
    public static IEnumerable<string> UserBinDirectories()
    {
        var home = HomeDirectory();
        if (home is not null)
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, "bin");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                yield return Path.Combine(appData, "npm");

            if (home is not null)
                yield return Path.Combine(home, "scoop", "shims");
        }
    }

    /// <summary>The user's home folder, or <see langword="null"/> when the OS doesn't report one.</summary>
    public static string? HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : home;
    }

    private static string[] WindowsExtensions()
    {
        // On Windows, an extensionless PATH match is typically a bash-style shim (e.g. npm
        // installs `opencode` alongside `opencode.cmd`; the extensionless file is a shebang
        // script only runnable from a POSIX shell). Process.Start with UseShellExecute = false
        // cannot execute it, so we only try executables that the OS will accept — PATHEXT
        // entries, with ".exe" / ".cmd" / ".bat" / ".ps1" as defaults if PATHEXT is absent.
        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        if (!string.IsNullOrEmpty(pathext))
        {
            return pathext
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .ToArray();
        }
        return [".exe", ".cmd", ".bat", ".ps1"];
    }
}
