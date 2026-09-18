namespace WeaveFleet.Infrastructure.Harnesses.OpenCode;

/// <summary>Where Fleet finds the <c>opencode</c> executable. Spawning and the availability check use the same lookup.</summary>
internal static class OpenCodeExecutable
{
    public const string Command = "opencode";

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
}
