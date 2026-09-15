using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.Configuration;

/// <summary>
/// One Fleet per database. Startup assumes it is the only Fleet using its database: it kills the agents the
/// previous run left behind and marks every session stopped. A second Fleet on the same database would kill the
/// first one's live agents, so Fleet holds an exclusive lock on <c>&lt;db name&gt;.lock</c> next to the database
/// for as long as it runs. The operating system releases the lock when the process ends, however it ends.
/// </summary>
/// <remarks>
/// While it runs, Fleet also writes <c>&lt;db name&gt;.instance.json</c> next to the lock, so the desktop app
/// can find a Fleet that is already running and connect to it. Only the lock holder writes it, so one left
/// behind by a crash is stale and gets replaced.
/// </remarks>
public sealed class FleetInstanceLock : IDisposable
{
    /// <summary>Exit code when another Fleet holds the lock (<c>EX_TEMPFAIL</c>). The desktop app recognises it.</summary>
    public const int InUseExitCode = 75;

    private readonly FileStream _lock;
    private bool _disposed;

    private FleetInstanceLock(FileStream lockStream, string instanceFilePath)
    {
        _lock = lockStream;
        InstanceFilePath = instanceFilePath;
    }

    public string InstanceFilePath { get; }

    public static string GetLockPath(string databasePath) =>
        Path.ChangeExtension(Path.GetFullPath(databasePath), ".lock");

    public static string GetInstanceFilePath(string databasePath) =>
        Path.ChangeExtension(Path.GetFullPath(databasePath), ".instance.json");

    /// <summary>Takes the lock for <paramref name="databasePath"/>, or returns null when another process holds it.</summary>
    public static FleetInstanceLock? TryAcquire(string databasePath)
    {
        var lockPath = GetLockPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        FileStream stream;
        try
        {
            // FileShare.None is an exclusive flock() on Unix and a sharing-mode lock on Windows.
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        var instance = new FleetInstanceLock(stream, GetInstanceFilePath(databasePath));
        instance.DeleteInstanceFile();
        return instance;
    }

    /// <summary>
    /// Whether a Fleet is running on <paramref name="databasePath"/> right now. A lock file that can't be opened
    /// counts as held, so callers err on the side of leaving the database alone.
    /// </summary>
    public static bool IsHeld(string databasePath)
    {
        try
        {
            using var probe = new FileStream(GetLockPath(databasePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The instance file for <paramref name="databasePath"/>, or null when there is none or it can't be read.</summary>
    public static FleetInstanceInfo? ReadInstance(string databasePath)
    {
        try
        {
            var json = File.ReadAllText(GetInstanceFilePath(databasePath));
            return JsonSerializer.Deserialize(json, FleetInstanceJsonContext.Default.FleetInstanceInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the instance file. Readable only by the current user on Unix.</summary>
    public void WriteInstanceFile(FleetInstanceInfo info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var json = JsonSerializer.Serialize(info, FleetInstanceJsonContext.Default.FleetInstanceInfo);
        var temporaryPath = InstanceFilePath + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temporaryPath, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(json);

        File.Move(temporaryPath, InstanceFilePath, overwrite: true);
    }

    /// <summary>Deletes the instance file and releases the lock. The lock file stays, so nobody locks a stale copy.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DeleteInstanceFile();
        _lock.Dispose();
    }

    private void DeleteInstanceFile()
    {
        try
        {
            File.Delete(InstanceFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next lock holder replaces it.
        }
    }
}

/// <summary>What a running Fleet writes to its instance file.</summary>
/// <param name="Pid">Fleet's process id.</param>
/// <param name="Url">Fleet's URL over loopback, e.g. <c>http://127.0.0.1:2113</c>.</param>
/// <param name="Version">Fleet's version.</param>
/// <param name="DatabasePath">The database Fleet is using.</param>
/// <param name="Desktop">Whether the desktop app started this Fleet.</param>
/// <param name="StartedAt">When Fleet started listening.</param>
public sealed record FleetInstanceInfo(
    int Pid,
    string Url,
    string Version,
    string DatabasePath,
    bool Desktop,
    DateTimeOffset StartedAt);

[JsonSerializable(typeof(FleetInstanceInfo))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class FleetInstanceJsonContext : JsonSerializerContext
{
}
