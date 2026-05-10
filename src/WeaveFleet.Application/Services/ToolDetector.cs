using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Detects which tools from the registry are installed on the current system.
/// Results are cached with a 5-minute TTL.
/// </summary>
public sealed class ToolDetector : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private List<ToolDefinition>? _cache;
    private DateTime _cacheTime = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

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

            var tasks = candidates.Select(t => DetectToolAsync(t, platform, ct));
            var results = await Task.WhenAll(tasks);

            _cache = results.Where(t => t is not null).Cast<ToolDefinition>().ToList();
            _cacheTime = DateTime.UtcNow;

            return Resolve(_cache);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static List<ResolvedTool> Resolve(List<ToolDefinition> tools) =>
        tools.Select(t => new ResolvedTool(t.Id, t.Label, t.IconName, t.Category)).ToList();

    private static async Task<ToolDefinition?> DetectToolAsync(
        ToolDefinition tool, string platform, CancellationToken ct)
    {
        if (tool.AlwaysAvailable) return tool;

        // Check binaries on PATH
        var binaries = tool.DetectBinaries?.GetValueOrDefault(platform);
        if (binaries is { Length: > 0 })
        {
            foreach (var bin in binaries)
            {
                if (await IsBinaryOnPathAsync(bin, platform, ct))
                    return tool;
            }
        }
        else
        {
            // Fallback: try the command itself
            if (tool.Platforms.TryGetValue(platform, out var cmd) && cmd.Command != "open")
            {
                if (await IsBinaryOnPathAsync(cmd.Command, platform, ct))
                    return tool;
            }
        }

        return null;
    }

    private static async Task<bool> IsBinaryOnPathAsync(string binary, string platform, CancellationToken ct)
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
            if (proc is null) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);

            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
