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
/// </summary>
internal static class ExecutableResolver
{
    /// <summary>
    /// Return an absolute path to <paramref name="name"/> on <c>PATH</c>, or the original name
    /// when no candidate is found (callers should treat the latter as "not found").
    /// </summary>
    public static string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;

        // Absolute or relative path already — trust the caller.
        if (name.Contains('/') || name.Contains('\\')) return name;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return name;
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var directories = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsExtensions()
            : [""]; // Non-Windows: try the bare name.

        foreach (var dir in directories)
        {
            foreach (var ext in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(dir, name + ext); }
                catch (ArgumentException) { continue; } // skip malformed PATH entries
                if (File.Exists(candidate)) return candidate;
            }
        }
        return name;
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
