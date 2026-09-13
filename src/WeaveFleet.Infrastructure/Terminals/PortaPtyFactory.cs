using System.Collections;
using Porta.Pty;
using WeaveFleet.Application.Terminals;

namespace WeaveFleet.Infrastructure.Terminals;

/// <summary>
/// <see cref="IPtyFactory"/> on Porta.Pty: a native shim forks and execs on Linux and macOS, and ConPTY
/// runs the process on Windows. See the terminal plan's Task 0 findings.
/// </summary>
internal sealed class PortaPtyFactory : IPtyFactory
{
    public async Task<IPtyProcess> SpawnAsync(PtySpawnOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var connection = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "fleet",
            Cols = options.Cols,
            Rows = options.Rows,
            Cwd = options.WorkingDirectory,
            App = options.Shell,
            CommandLine = [.. options.Args],
            Environment = EnvironmentChanges(options.Environment),
        }, ct).ConfigureAwait(false);

        return new PortaPtyProcess(connection);
    }

    /// <summary>
    /// Porta.Pty starts from Fleet's own environment and applies changes, where an empty value removes a
    /// variable. Turn the child's whole environment into those changes.
    /// </summary>
    internal static Dictionary<string, string> EnvironmentChanges(IReadOnlyDictionary<string, string> target)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changes = new Dictionary<string, string>(comparer);

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && !target.ContainsKey(key))
                changes[key] = string.Empty;
        }

        foreach (var (key, value) in target)
            changes[key] = value;

        return changes;
    }
}
