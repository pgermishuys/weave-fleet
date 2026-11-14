namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>What the live tests with a real <c>opencode</c> share: a scratch OpenCode home (Fleet on Kestrel is <see cref="KestrelFleetFactory"/>).</summary>
internal static class PooledOpenCodeLiveHost
{
    /// <summary>
    /// Reads <paramref name="session"/>'s events until <paramref name="ct"/> ends, as Fleet's relay reads every session's.
    /// OpenCode asks before a shell command or an edit, and the session answers the ask as it reads it.
    /// </summary>
    public static Task ReadEventsAsync(WeaveFleet.Domain.Harnesses.IHarnessSession session, CancellationToken ct)
        => Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in session.SubscribeAsync(ct)) { }
            }
            catch (OperationCanceledException)
            {
                // The test is over.
            }
        }, CancellationToken.None);

    /// <summary>
    /// A user config that points OpenCode at the fake model and loads <paramref name="pluginPath"/> as a plugin of
    /// the user's own. Returns the env that points the pooled process at it. A <paramref name="contextLimit"/> gives the
    /// fake model a context window, so OpenCode compacts once a turn reports more tokens than that. The config folder
    /// starts with the packages OpenCode would otherwise install on its first request (see <see cref="OpenCodePluginDependencies"/>).
    /// </summary>
    public static Dictionary<string, string> WriteScratchOpenCodeHome(string root, Uri llmBaseUrl, string pluginPath, int? contextLimit = null, bool seedPluginDependencies = true)
    {
        RequireRipgrepWhereRequired();

        var dirs = new Dictionary<string, string>
        {
            ["HOME"] = Path.Combine(root, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
        };
        foreach (var dir in dirs.Values)
            Directory.CreateDirectory(dir);

        // A fresh HOME makes OpenCode fetch and install things on its first request, which can take
        // minutes. None of it matters here: the model is local and the plugins are files.
        dirs["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        dirs["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
        dirs["OPENCODE_DISABLE_MODELS_FETCH"] = "true";
        dirs["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
        dirs["OPENCODE_DISABLE_SHARE"] = "true";

        var configDir = Path.Combine(dirs["XDG_CONFIG_HOME"], "opencode");
        Directory.CreateDirectory(configDir);
        if (seedPluginDependencies)
            OpenCodePluginDependencies.Seed(configDir);
        var baseUrl = llmBaseUrl.ToString().TrimEnd('/') + "/v1";
        var limit = contextLimit is { } context ? $$""", "limit": { "context": {{context}}, "output": 1000 }""" : "";
        File.WriteAllText(Path.Combine(configDir, "opencode.json"), $$"""
            {
              "provider": {
                "fake": {
                  "npm": "@ai-sdk/openai-compatible",
                  "options": { "baseURL": "{{baseUrl}}", "apiKey": "fake-key" },
                  "models": { "fake-model": { "tool_call": true{{limit}} } }
                }
              },
              "model": "fake/fake-model",
              "small_model": "fake/fake-model",
              "plugin": ["{{new Uri(pluginPath).AbsoluteUri}}"]
            }
            """);

        return dirs;
    }

    /// <summary>
    /// OpenCode's skill, grep and glob tools run ripgrep: the <c>rg</c> on PATH, else a copy OpenCode downloads from
    /// GitHub into its data folder. Each scratch home's data folder starts empty, so without <c>rg</c> on PATH every
    /// test that loads a skill downloads ripgrep again, and when GitHub is slow the skill comes back as "ripgrep
    /// execution failed" instead of its text. With <c>FLEET_REQUIRE_OPENCODE=1</c> (as in CI) that's an error up front;
    /// elsewhere OpenCode downloads it as before.
    /// </summary>
    private static void RequireRipgrepWhereRequired()
    {
        if (Environment.GetEnvironmentVariable("FLEET_REQUIRE_OPENCODE") != "1" || IsRipgrepOnPath())
            return;
        throw new InvalidOperationException(
            "The OpenCode live tests need ripgrep on PATH: without it OpenCode downloads ripgrep from GitHub for every " +
            "test, and a skill fails with \"ripgrep execution failed\" when the download does. Install ripgrep (CI does, in ci.yml).");
    }

    private static bool IsRipgrepOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, name)));
    }
}
