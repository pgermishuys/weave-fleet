using System.Diagnostics;
using System.Text.RegularExpressions;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// The packages OpenCode installs into a user config folder that loads a plugin. It runs <c>npm install</c> of
/// <c>@opencode-ai/plugin</c> (about 60 MB) inside the first <c>POST /session</c> for a folder, which took 40 to
/// 100 s here and went past Fleet's 100 s request timeout on CI. Every live test starts from an empty scratch
/// config folder, so each one paid it. This installs them once per test run, in a throwaway OpenCode, and the
/// scratch folders start with a copy: OpenCode finds the packages and doesn't install again.
/// </summary>
internal static partial class OpenCodePluginDependencies
{
    private static readonly string[] Entries = ["package.json", "package-lock.json", ".gitignore", "node_modules"];
    private static readonly object Gate = new();
    private static string? _installed;
    private static bool _tried;

    [GeneratedRegex(@"opencode server listening on http://(?<host>[^:]+):(?<port>\d+)")]
    private static partial Regex ListeningLine();

    /// <summary>Copies the installed packages into <paramref name="configDir"/>; does nothing if they couldn't be installed.</summary>
    public static void Seed(string configDir)
    {
        var source = Installed();
        if (source is null)
            return;

        foreach (var entry in Entries)
        {
            var from = Path.Combine(source, entry);
            var to = Path.Combine(configDir, entry);
            if (Directory.Exists(from))
                CopyDirectory(from, to);
            else if (File.Exists(from))
                File.Copy(from, to, overwrite: true);
        }
    }

    private static string? Installed()
    {
        lock (Gate)
        {
            if (_tried)
                return _installed;

            _tried = true;
            try
            {
                _installed = Install();
            }
            catch (Exception)
            {
                // Without them each test installs its own, as before.
                _installed = null;
            }

            return _installed;
        }
    }

    private static string Install()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fleet-live-opencode-deps-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        };
        var plugin = Path.Combine(root, "no-op.ts");
        File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");

        // The same scratch home the tests use, minus the packages: its plugin is what makes OpenCode install them.
        var environment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, new Uri("http://127.0.0.1:9"), plugin, seedPluginDependencies: false);
        var configDir = Path.Combine(environment["XDG_CONFIG_HOME"], "opencode");

        var start = new ProcessStartInfo(OpenCodeExecutable.Resolve())
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "serve", "--hostname", "127.0.0.1", "--port", "0" })
            start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("opencode didn't start.");
        try
        {
            var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null && ListeningLine().Match(e.Data) is { Success: true } match)
                    listening.TrySetResult(int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture));
            };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var port = listening.Task.WaitAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();

            // The first session for a folder is what installs the packages; the request waits for them.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var response = http.PostAsync($"http://127.0.0.1:{port}/session?directory={Uri.EscapeDataString(workspace)}", content).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            process.WaitForExit(10_000);
        }

        if (!Directory.Exists(Path.Combine(configDir, "node_modules", "@opencode-ai", "plugin")))
            throw new InvalidOperationException("OpenCode didn't install its plugin packages.");

        return configDir;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyDirectory(directory, Path.Combine(to, Path.GetFileName(directory)));
    }
}
