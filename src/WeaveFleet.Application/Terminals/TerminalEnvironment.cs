using System.Collections;

namespace WeaveFleet.Application.Terminals;

/// <summary>
/// The environment a terminal's shell gets: Fleet's own, minus what belongs to Fleet. Fleet's settings
/// (secrets included) arrive as <c>Fleet__*</c>, the launchers set <c>ASPNETCORE_*</c> and a bare
/// <c>URLS</c> (a shell that kept either would move the user's own <c>dotnet run</c> onto Fleet's port, and
/// <c>URLS</c> outranks the launch profile), the launcher sets <c>WEAVE_FLEET_*</c>, and OpenCode's server
/// credentials never belong in a shell. <c>ELECTRON_*</c> comes from the desktop app (or an editor Fleet was
/// started from): a shell that kept <c>ELECTRON_RUN_AS_NODE</c> would start every Electron app as plain Node.
/// </summary>
public static class TerminalEnvironment
{
    private static readonly string[] RemovedPrefixes = ["Fleet__", "FLEET_", "WEAVE_FLEET_", "ASPNETCORE_", "ELECTRON_"];

    private static readonly string[] RemovedNames =
    [
        "URLS",
        "DOTNET_ENVIRONMENT",
        "DOTNET_URLS",
        "OPENCODE_SERVER_PASSWORD",
        "OPENCODE_SERVER_USERNAME",
        "OPENCODE_CONFIG_CONTENT",
    ];

    private static readonly string[] AgentNames = ["FLEET_URL"];

    private static readonly string[] HarnessServerSettings = ["OPENCODE_CONFIG", "OPENCODE_CONFIG_DIR", "OPENCODE_DB"];

    public static Dictionary<string, string> Build(IDictionary source, bool windows)
    {
        ArgumentNullException.ThrowIfNull(source);

        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is not string key || entry.Value is not string value)
                continue;
            if (IsFleetOwned(key))
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

    /// <summary>
    /// Removes Fleet's variables from a child's environment, such as a <see cref="System.Diagnostics.ProcessStartInfo"/>'s,
    /// which starts as a copy of Fleet's own. Call it before setting the child's own variables.
    /// </summary>
    public static void RemoveFleetOwned(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var key in environment.Keys.Where(IsFleetOwned).ToList())
            environment.Remove(key);
    }

    /// <summary>
    /// Whether a variable belongs to Fleet and must not reach a process Fleet starts for the user: terminals, app
    /// runs, and agent harnesses, whose shell tools pass it on to every command the agent runs.
    /// </summary>
    public static bool IsFleetOwned(string key)
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

    /// <summary>
    /// What an agent's shell commands must not keep of the variables Fleet set for its harness server
    /// (<paramref name="setForServer"/>): each name maps to <see langword="null"/> to remove it, or to the value the
    /// server <paramref name="inherited"/> from Fleet to put back. Fleet's own variables (the server's password, its
    /// config, the bridge token) are removed, except what's meant for the agent: <c>FLEET_URL</c>, which Fleet's skills
    /// call it through. The settings that say which config and data OpenCode reads go back to the user's own, or away:
    /// an <c>opencode</c> the agent runs must not open the server's database. Anything else Fleet set stays.
    /// </summary>
    public static Dictionary<string, string?> AgentShellChanges(IDictionary inherited, IEnumerable<string> setForServer)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(setForServer);

        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in setForServer)
        {
            if (IsForTheAgent(key))
                continue;
            if (IsFleetOwned(key))
                changes[key] = null;
            else if (IsHarnessServerSetting(key))
                changes[key] = inherited[key] as string;
        }

        return changes;
    }

    /// <summary>Variables Fleet sets for a harness server that its agent needs too.</summary>
    public static bool IsForTheAgent(string key) => AgentNames.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Settings that say which config and data an OpenCode process reads. A user may set them for their own OpenCode,
    /// so they aren't Fleet's and pass through to what Fleet starts; the values Fleet sets for a server stay with it.
    /// </summary>
    public static bool IsHarnessServerSetting(string key) => HarnessServerSettings.Contains(key, StringComparer.OrdinalIgnoreCase);
}
