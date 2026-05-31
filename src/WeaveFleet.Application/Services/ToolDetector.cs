using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Detects which tools from the registry are installed on the current system.
/// Results are cached with a 5-minute TTL.
/// </summary>
public sealed partial class ToolDetector : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly ILogger<ToolDetector> _logger;
    private List<ToolDefinition>? _cache;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ToolDetector(ILogger<ToolDetector> logger)
    {
        _logger = logger;
    }

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<ResolvedTool>> DetectAsync(CancellationToken ct = default)
    {
        if (_cache is not null && DateTime.UtcNow - _cacheTime < CacheTtl)
            return Resolve(_cache);

        await _lock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cache is not null && DateTime.UtcNow - _cacheTime < CacheTtl)
                return Resolve(_cache);

            var platform = ToolRegistry.PlatformKey;
            var candidates = ToolRegistry.BuiltinTools
                .Where(t => t.Platforms.ContainsKey(platform))
                .ToList();

            LogDetectionStarted(_logger, platform, candidates.Count);

            var tasks = candidates.Select(t => DetectToolAsync(t, platform, ct));
            var results = await Task.WhenAll(tasks);

            _cache = results.Where(t => t is not null).Cast<ToolDefinition>().ToList();
            _cacheTime = DateTime.UtcNow;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                var toolIds = string.Join(", ", _cache.Select(t => t.Id));
                LogDetectionComplete(_logger, _cache.Count, candidates.Count, toolIds);
            }

            return Resolve(_cache);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<ResolvedTool> Resolve(List<ToolDefinition> tools) =>
        tools.Select(t => new ResolvedTool(t.Id, t.Label, t.IconName, t.Category)).ToList();

    private async Task<ToolDefinition?> DetectToolAsync(
        ToolDefinition tool, string platform, CancellationToken ct)
    {
        if (tool.AlwaysAvailable)
        {
            LogToolAlwaysAvailable(_logger, tool.Id);
            return tool;
        }

        // Check binaries on PATH
        var binaries = tool.DetectBinaries?.GetValueOrDefault(platform);
        if (binaries is { Length: > 0 })
        {
            foreach (var bin in binaries)
            {
                if (await IsBinaryOnPathAsync(bin, platform, ct))
                {
                    LogToolDetectedViaBinary(_logger, tool.Id, bin);
                    return tool;
                }
            }
        }
        else
        {
            // Fallback: try the command itself
            if (tool.Platforms.TryGetValue(platform, out var cmd) && cmd.Command != "open")
            {
                if (await IsBinaryOnPathAsync(cmd.Command, platform, ct))
                {
                    LogToolDetectedViaCommand(_logger, tool.Id, cmd.Command);
                    return tool;
                }
            }
        }

        // Special case: Visual Studio detection via vswhere on Windows
        if (tool.Id == "visual-studio" && platform == "win32")
        {
            if (await DetectVisualStudioViaVsWhereAsync(ct))
            {
                LogToolDetectedViaBinary(_logger, tool.Id, "vswhere");
                return tool;
            }
        }

        LogToolNotFound(_logger, tool.Id);
        return null;
    }

    private async Task<bool> DetectVisualStudioViaVsWhereAsync(CancellationToken ct)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vswherePath = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");

            if (!File.Exists(vswherePath))
                return false;

            var psi = new ProcessStartInfo(vswherePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-latest");
            psi.ArgumentList.Add("-property");
            psi.ArgumentList.Add("productPath");
            psi.ArgumentList.Add("-requires");
            psi.ArgumentList.Add("Microsoft.Component.MSBuild");

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception ex)
        {
            LogProbeFailed(_logger, "vswhere", ex);
            return false;
        }
    }

    private async Task<bool> IsBinaryOnPathAsync(string binary, string platform, CancellationToken ct)
    {
        try
        {
            var (probeCmd, probeArgs) = platform == "win32"
                ? ("where.exe", binary)
                : ("which", binary);

            var psi = new ProcessStartInfo(probeCmd)
            {
                Arguments = probeArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LogProbeProcessFailed(_logger, binary);
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LogProbeFailed(_logger, binary, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool detection: platform={Platform}, candidates={Count}")]
    private static partial void LogDetectionStarted(ILogger logger, string platform, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool detection complete: {Detected}/{Candidates} tools found ({Tools})")]
    private static partial void LogDetectionComplete(ILogger logger, int detected, int candidates, string tools);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool '{ToolId}' is always available")]
    private static partial void LogToolAlwaysAvailable(ILogger logger, string toolId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool '{ToolId}' detected via binary '{Binary}'")]
    private static partial void LogToolDetectedViaBinary(ILogger logger, string toolId, string binary);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool '{ToolId}' detected via command '{Command}'")]
    private static partial void LogToolDetectedViaCommand(ILogger logger, string toolId, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tool '{ToolId}' not found")]
    private static partial void LogToolNotFound(ILogger logger, string toolId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to start probe process for '{Binary}'")]
    private static partial void LogProbeProcessFailed(ILogger logger, string binary);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Probe failed for binary '{Binary}'")]
    private static partial void LogProbeFailed(ILogger logger, string binary, Exception ex);
}
