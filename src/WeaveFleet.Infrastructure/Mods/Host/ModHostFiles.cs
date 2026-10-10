using System.Reflection;
using WeaveFleet.Application.Mods.Host;

namespace WeaveFleet.Infrastructure.Mods.Host;

/// <summary>
/// Finds the mod host's script. Installed, it is <c>{app}/mods-host/host.js</c> beside the binary (the build and publish
/// output both put it there). Running from the repository (tests, <c>dotnet run</c>) it is <c>mods/host/dist/host.js</c>,
/// found by walking up from the binary to the folder that holds <c>WeaveFleet.slnx</c>. A path given in configuration wins
/// when the file exists. A script that isn't there yet is looked for again on the next call, so building the host while
/// Fleet runs is picked up; once found, the path is kept.
/// </summary>
public sealed class ModHostFiles : IModHostFiles
{
    private const int MaxLevelsUp = 8;

    private readonly string _baseDirectory;
    private readonly string? _overridePath;
    private string? _found;

    /// <param name="baseDirectory">The folder Fleet's binary is in (<c>AppContext.BaseDirectory</c>).</param>
    /// <param name="overridePath">A script to use instead of the shipped one, from configuration or a test; null for none.</param>
    public ModHostFiles(string baseDirectory, string? overridePath = null)
    {
        _baseDirectory = baseDirectory;
        _overridePath = overridePath;
    }

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
        if (!string.IsNullOrWhiteSpace(_overridePath) && File.Exists(_overridePath))
            return _overridePath;

        var installed = Path.Combine(_baseDirectory, "mods-host", "host.js");
        if (File.Exists(installed))
            return installed;

        var directory = new DirectoryInfo(_baseDirectory);
        for (var level = 0; directory is not null && level <= MaxLevelsUp; level++, directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "WeaveFleet.slnx")))
                continue;

            var built = Path.Combine(directory.FullName, "mods", "host", "dist", "host.js");
            if (File.Exists(built))
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
