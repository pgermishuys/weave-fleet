namespace WeaveFleet.Application.Terminals;

/// <summary>A shell to try, and the name its tab gets.</summary>
public sealed record ShellCandidate(string Path, IReadOnlyList<string> Args, string Name);

/// <summary>
/// Which shell a terminal runs, in order of preference. If one fails to start, the next is tried.
/// </summary>
public static class TerminalShell
{
    /// <summary>
    /// Unix: <c>$SHELL</c>, then zsh, bash and sh. zsh and bash start as login shells so they read the
    /// user's profile: Fleet often runs as a service with a bare <c>PATH</c>. Windows: PowerShell 7, then
    /// Windows PowerShell, then <c>cmd.exe</c>.
    /// </summary>
    public static IReadOnlyList<ShellCandidate> Candidates(
        IReadOnlyDictionary<string, string> env,
        bool windows,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(fileExists);

        var candidates = new List<ShellCandidate>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !fileExists(path))
                return;
            if (candidates.Any(c => string.Equals(c.Path, path, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                return;
            candidates.Add(Describe(path, windows));
        }

        if (windows)
        {
            Add(FindOnPath("pwsh.exe", env, fileExists));
            var systemRoot = Get(env, "SystemRoot") ?? @"C:\Windows";
            Add(System.IO.Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            Add(Get(env, "ComSpec") ?? System.IO.Path.Combine(systemRoot, "System32", "cmd.exe"));
        }
        else
        {
            Add(Get(env, "SHELL"));
            Add("/bin/zsh");
            Add("/usr/bin/zsh");
            Add("/bin/bash");
            Add("/usr/bin/bash");
            Add("/bin/sh");
        }

        return candidates;
    }

    private static ShellCandidate Describe(string path, bool windows)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        IReadOnlyList<string> args = name switch
        {
            "zsh" or "bash" when !windows => ["-l"],
            "pwsh" or "powershell" => ["-NoLogo"],
            _ => [],
        };
        return new ShellCandidate(path, args, name);
    }

    private static string? FindOnPath(string exe, IReadOnlyDictionary<string, string> env, Func<string, bool> fileExists)
    {
        var path = Get(env, "PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = System.IO.Path.Combine(dir, exe);
            if (fileExists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? Get(IReadOnlyDictionary<string, string> env, string key)
    {
        if (env.TryGetValue(key, out var value))
            return value;
        foreach (var (k, v) in env)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }
}
