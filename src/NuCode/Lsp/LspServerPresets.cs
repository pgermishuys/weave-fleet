using System.Diagnostics;
using System.Runtime.InteropServices;
using NuCode.Configuration;

namespace NuCode.Lsp;

/// <summary>
/// Defines a built-in LSP server preset that can be auto-detected on PATH.
/// </summary>
public sealed record LspServerPreset
{
    /// <summary>The preset name (used as the config key).</summary>
    public required string Name { get; init; }

    /// <summary>The command to start the LSP server.</summary>
    public required List<string> Command { get; init; }

    /// <summary>File extensions this server handles.</summary>
    public required List<string> Extensions { get; init; }

    /// <summary>Environment variables to set when starting the server.</summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>Initialization options to send during initialize.</summary>
    public Dictionary<string, object?>? Initialization { get; init; }

    /// <summary>Converts this preset to an <see cref="LspServerConfig"/>.</summary>
    public LspServerConfig ToConfig() => new()
    {
        Command = Command,
        Extensions = Extensions,
        Env = Env,
        Initialization = Initialization,
    };
}

/// <summary>
/// Built-in LSP server presets for common languages. Detects available servers on PATH.
/// </summary>
internal static class LspServerPresets
{
    /// <summary>All known LSP server presets.</summary>
    internal static IReadOnlyList<LspServerPreset> All { get; } =
    [
        new() { Name = "gopls", Command = ["gopls"], Extensions = [".go"] },
        new() { Name = "rust-analyzer", Command = ["rust-analyzer"], Extensions = [".rs"] },
        new() { Name = "typescript-language-server", Command = ["typescript-language-server", "--stdio"], Extensions = [".ts", ".tsx", ".js", ".jsx"] },
        new() { Name = "pyright", Command = ["pyright-langserver", "--stdio"], Extensions = [".py"] },
        new() { Name = "csharp-ls", Command = ["csharp-ls"], Extensions = [".cs"] },
        new() { Name = "clangd", Command = ["clangd"], Extensions = [".c", ".cpp", ".h", ".hpp"] },
        new() { Name = "zls", Command = ["zls"], Extensions = [".zig"] },
        new() { Name = "lua-language-server", Command = ["lua-language-server"], Extensions = [".lua"] },
        new() { Name = "solargraph", Command = ["solargraph", "stdio"], Extensions = [".rb"] },
        new() { Name = "jdtls", Command = ["jdtls"], Extensions = [".java"] },
    ];

    /// <summary>
    /// Fallback presets for languages where multiple servers exist.
    /// Keyed by the primary preset name, value is the fallback preset.
    /// </summary>
    private static readonly Dictionary<string, LspServerPreset> Fallbacks = new()
    {
        ["pyright"] = new() { Name = "pylsp", Command = ["pylsp"], Extensions = [".py"] },
        ["csharp-ls"] = new() { Name = "omnisharp", Command = ["OmniSharp", "-lsp", "--stdio"], Extensions = [".cs"] },
    };

    /// <summary>
    /// Detects which presets have their server binary available on PATH.
    /// For presets with fallbacks, checks the primary first, then the fallback.
    /// </summary>
    internal static async Task<IReadOnlyList<LspServerPreset>> DetectAvailableAsync(CancellationToken ct)
    {
        var tasks = All.Select(async preset =>
        {
            ct.ThrowIfCancellationRequested();

            if (await IsCommandAvailableAsync(preset.Command[0], ct))
            {
                return preset;
            }

            if (Fallbacks.TryGetValue(preset.Name, out var fallback) &&
                await IsCommandAvailableAsync(fallback.Command[0], ct))
            {
                return fallback;
            }

            return null;
        }).ToList();

        var detected = await Task.WhenAll(tasks);
        return detected.Where(p => p is not null).ToList()!;
    }

    /// <summary>
    /// Merges detected presets with explicit user config. Explicit config always wins.
    /// </summary>
    internal static Dictionary<string, LspServerConfig> MergeWithConfig(
        IReadOnlyList<LspServerPreset> detectedPresets,
        Dictionary<string, LspServerConfig>? explicitConfig)
    {
        var merged = new Dictionary<string, LspServerConfig>(StringComparer.OrdinalIgnoreCase);

        // Add detected presets first (as defaults)
        foreach (var preset in detectedPresets)
        {
            merged[preset.Name] = preset.ToConfig();
        }

        // Overlay explicit config (wins over presets)
        if (explicitConfig is not null)
        {
            foreach (var (name, config) in explicitConfig)
            {
                merged[name] = config;
            }
        }

        // Remove disabled entries
        var disabledKeys = merged
            .Where(kvp => kvp.Value.Disabled == true)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in disabledKeys)
        {
            merged.Remove(key);
        }

        return merged;
    }

    private static async Task<bool> IsCommandAvailableAsync(string command, CancellationToken ct)
    {
        try
        {
            var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "where" : "which",
                    Arguments = command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            // Use WaitForExitAsync with a timeout to avoid hanging
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { /* best effort */ }
                return false;
            }
        }
        catch
        {
            return false;
        }
    }
}
