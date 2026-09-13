using System.Collections;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// The environment a terminal's shell gets: Fleet's own, minus what belongs to Fleet. Fleet's settings
/// (secrets included) arrive as <c>Fleet__*</c>, the service sets <c>ASPNETCORE_*</c> (a shell that kept
/// <c>ASPNETCORE_URLS</c> would move the user's own <c>dotnet run</c> onto Fleet's port), the launcher sets
/// <c>WEAVE_FLEET_*</c>, and OpenCode's server credentials never belong in a shell.
/// </summary>
public static class TerminalEnvironment
{
    private static readonly string[] RemovedPrefixes = ["Fleet__", "FLEET_", "WEAVE_FLEET_", "ASPNETCORE_"];

    private static readonly string[] RemovedNames =
    [
        "DOTNET_ENVIRONMENT",
        "DOTNET_URLS",
        "OPENCODE_SERVER_PASSWORD",
        "OPENCODE_SERVER_USERNAME",
        "OPENCODE_CONFIG_CONTENT",
    ];

    public static Dictionary<string, string> Build(IDictionary source, bool windows)
    {
        ArgumentNullException.ThrowIfNull(source);

        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string key || entry.Value is not string value)
                continue;
            if (IsRemoved(key))
                continue;
            env[key] = value;
        }

        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "WeaveFleet";

        // A service started without a locale gives the shell a 7-bit one, and every non-ASCII character
        // comes out as '?'. Only fill it in when nothing is set.
        if (!windows && !env.ContainsKey("LANG") && !env.ContainsKey("LC_ALL") && !env.ContainsKey("LC_CTYPE"))
            env["LANG"] = "C.UTF-8";

        return env;
    }

    private static bool IsRemoved(string key)
    {
        foreach (var prefix in RemovedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var name in RemovedNames)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
