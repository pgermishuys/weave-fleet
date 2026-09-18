using System.Text.RegularExpressions;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>Builds commands to type into the user's terminal: POSIX shells elsewhere, PowerShell on Windows.</summary>
internal static partial class ShellCommand
{
    /// <summary>
    /// <paramref name="executable"/> as the first word of a command, quoted when it has spaces or quotes.
    /// PowerShell only runs a quoted path through the call operator, so it gets <c>&amp; '…'</c>.
    /// </summary>
    public static string Executable(string executable, bool windows) =>
        (windows ? SafeWindowsWord() : SafePosixWord()).IsMatch(executable)
            ? executable
            : windows
                ? $"& '{executable.Replace("'", "''", StringComparison.Ordinal)}'"
                : $"'{executable.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    [GeneratedRegex(@"^[\w./:@%+=-]+$")]
    private static partial Regex SafePosixWord();

    [GeneratedRegex(@"^[\w./:\\@%+=-]+$")]
    private static partial Regex SafeWindowsWord();
}
