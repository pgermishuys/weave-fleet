using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>
/// A file per harness process a Fleet started on Linux or macOS, naming that Fleet and the process by
/// <see cref="ProcessIdentity"/>, so a harness left behind when its Fleet died without stopping it (killed, crashed,
/// its test host torn down) is stopped by the next Fleet that starts with the same folder. The file goes when the
/// process exits. On Windows the harness is in a Job Object that the system closes, killing it, when Fleet dies.
/// <para>
/// A leftover is stopped only when both its Fleet is gone and the process running under its pid is the one the
/// file names. A pid alone proves nothing: once a process exits, its pid can go to anything.
/// </para>
/// </summary>
internal sealed partial class HarnessProcessRecords
{
    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Lazy<ProcessIdentity?> _owner;

    public HarnessProcessRecords(string directory, ILogger logger)
    {
        _directory = directory;
        _logger = logger;
        _owner = new Lazy<ProcessIdentity?>(() => Owner ?? ReadIdentity(Environment.ProcessId));
    }

    public string Directory => _directory;

    /// <summary>The Fleet whose processes this records; this process unless a test says otherwise.</summary>
    internal ProcessIdentity? Owner { get; init; }

    /// <summary>Reads a process's identity; <see cref="ProcessIdentity.Read"/> unless a test says otherwise.</summary>
    internal Func<int, ProcessIdentity?> ReadIdentity { get; init; } = ProcessIdentity.Read;

    /// <summary>Kills a process and its children; a real kill unless a test says otherwise.</summary>
    internal Action<int> KillTree { get; init; } = KillProcessTree;

    /// <summary>Notes that this Fleet started <paramref name="process"/>, running <paramref name="executable"/>.</summary>
    public void Add(ProcessIdentity process, string executable)
    {
        if (_owner.Value is not { } owner)
            return;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            var path = PathFor(owner, process);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                new HarnessProcessRecord(owner, process, executable), HarnessProcessRecordJsonContext.Default.HarnessProcessRecord));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, process.Pid, _directory, ex);
        }
    }

    /// <summary>Forgets <paramref name="process"/>: it exited, or Fleet stopped it.</summary>
    public void Remove(ProcessIdentity process)
    {
        if (_owner.Value is not { } owner)
            return;

        try
        {
            File.Delete(PathFor(owner, process));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, process.Pid, _directory, ex);
        }
    }

    /// <summary>
    /// Stops each recorded process whose Fleet is gone and that is still the process the record names, with its
    /// children, and removes the records of processes that are gone. Records of a Fleet that's still running (another
    /// Fleet with the same folder, or this one) are left alone. Returns how many processes it stopped.
    /// </summary>
    public int StopLeftovers()
    {
        string[] files;
        try
        {
            files = System.IO.Directory.Exists(_directory) ? System.IO.Directory.GetFiles(_directory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, 0, _directory, ex);
            return 0;
        }

        var stopped = 0;
        foreach (var file in files)
        {
            var record = ReadRecord(file);
            if (record is null)
            {
                Delete(file);
                continue;
            }

            // Its Fleet is still running: the process is that Fleet's to stop.
            if (record.Owner.IsSameProcess(ReadIdentity(record.Owner.Pid)))
                continue;

            var current = ReadIdentity(record.Process.Pid);
            if (current is null)
            {
                Delete(file);
                continue;
            }

            if (!record.Process.IsSameProcess(current) || record.Process.Pid == Environment.ProcessId)
            {
                // The pid belongs to another process now.
                LogPidReused(_logger, record.Process.Pid, current.Name, record.Process.Name);
                Delete(file);
                continue;
            }

            try
            {
                LogStoppingLeftover(_logger, record.Process.Pid, record.Process.Name, record.Executable, record.Owner.Pid);
                KillTree(record.Process.Pid);
                stopped++;
                Delete(file);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
                                           or AggregateException or NotSupportedException)
            {
                LogStopFailed(_logger, record.Process.Pid, ex);
            }
        }

        return stopped;
    }

    private HarnessProcessRecord? ReadRecord(string file)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(file), HarnessProcessRecordJsonContext.Default.HarnessProcessRecord);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogUnreadable(_logger, file, ex);
            return null;
        }
    }

    private void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(_logger, 0, file, ex);
        }
    }

    private string PathFor(ProcessIdentity owner, ProcessIdentity process)
        => Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture,
            $"{owner.Pid}-{owner.StartTime}-{process.Pid}-{process.StartTime}.json"));

    private static void KillProcessTree(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping harness process {Pid} ({Name}, {Executable}): the Fleet that started it (pid {OwnerPid}) is gone")]
    private static partial void LogStoppingLeftover(ILogger logger, int pid, string name, string executable, int ownerPid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not stopping pid {Pid} ({Name}): it isn't the harness process {RecordedName} Fleet started, which is gone")]
    private static partial void LogPidReused(ILogger logger, int pid, string name, string recordedName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't stop leftover harness process {Pid}")]
    private static partial void LogStopFailed(ILogger logger, int pid, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read the harness process record {File}; removing it")]
    private static partial void LogUnreadable(ILogger logger, string file, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the record of harness process {Pid} in {Path}")]
    private static partial void LogWriteFailed(ILogger logger, int pid, string path, Exception ex);
}

/// <summary>What a record file holds: the Fleet, the process it started, and what that process runs.</summary>
internal sealed record HarnessProcessRecord(ProcessIdentity Owner, ProcessIdentity Process, string Executable);

/// <summary>What Fleet does about harness processes when it starts (see <see cref="ProcessGroupHelper"/>).</summary>
public static class HarnessProcessStartup
{
    /// <summary>
    /// On Linux and macOS: stops the harness processes a Fleet that's gone left running (see
    /// <see cref="HarnessProcessRecords"/>), then records each one this Fleet starts in the same folder,
    /// <paramref name="directory"/>. Returns how many it stopped. Nothing to do on Windows, where the system kills a
    /// Fleet's harnesses with it.
    /// </summary>
    public static int StopLeftoversAndRecord(string directory, ILogger logger)
    {
        if (!ProcessIdentity.IsSupported)
            return 0;

        var records = new HarnessProcessRecords(directory, logger);
        var stopped = records.StopLeftovers();
        ProcessGroupHelper.UseRecords(records);
        return stopped;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HarnessProcessRecord))]
internal sealed partial class HarnessProcessRecordJsonContext : JsonSerializerContext;
