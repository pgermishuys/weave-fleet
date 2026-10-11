using System.Reflection;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// Finds the mod host's script: the override when it exists, else <c>{app}/mods-host/host.js</c> beside the binary, else
/// (running from the repository) <c>mods/host/dist/host.js</c> in the folder up the tree that holds <c>WeaveFleet.slnx</c>.
/// A script that isn't there yet is looked for again on the next call; once found the path is kept.
/// </summary>
/// <param name="baseDirectory">The folder Fleet's binary is in (<c>AppContext.BaseDirectory</c>).</param>
/// <param name="overridePath">A script to use instead of the shipped one; null for none.</param>
public sealed class ModHostFiles(string baseDirectory, string? overridePath = null) : IModHostFiles
{
    private const int MaxLevelsUp = 8;

    private string? _found;

    /// <inheritdoc />
    public string? HostScript
    {
        get
        {
            var found = Volatile.Read(ref _found);
            if (found is not null && File.Exists(found))
                return found;

            found = Resolve();
            Volatile.Write(ref _found, found);
            return found;
        }
    }

    /// <inheritdoc />
    public string FleetVersion { get; } = ReadVersion();

    private string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var installed = Path.Combine(baseDirectory, "mods-host", "host.js");
        if (File.Exists(installed))
            return installed;

        var directory = new DirectoryInfo(baseDirectory);
        for (var level = 0; directory is not null && level <= MaxLevelsUp; level++, directory = directory.Parent)
        {
            var built = Path.Combine(directory.FullName, "mods", "host", "dist", "host.js");
            if (File.Exists(Path.Combine(directory.FullName, "WeaveFleet.slnx")) && File.Exists(built))
                return built;
        }

        return null;
    }

    private static string ReadVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ModHostFiles).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }
}
