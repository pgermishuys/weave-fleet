using System.Collections.Concurrent;
using System.Text.Json;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Infrastructure.Mods.Host;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// The real mod host for live tests: Bun (<c>FLEET_TEST_BUN</c>, <c>~/.bun/bin/bun</c>, or <c>bun</c> on PATH) running
/// <c>mods/host/dist/host.js</c> from this repository. Without them the live tests return early, unless
/// <c>FLEET_MODS_LIVE=1</c> (CI's mod host job), where a missing Bun or host.js fails them instead.
/// </summary>
internal static class RealModHost
{
    public const string Category = "ModHostLive";

    /// <summary>Bun and host.js, or null when this machine has no way to run the host (and live runs aren't required).</summary>
    public static (string Bun, string HostScript)? Find()
    {
        var bun = FindBun();
        var script = new ModHostFiles(AppContext.BaseDirectory).HostScript;
        if (bun is not null && script is not null)
            return (bun, script);
        if (Environment.GetEnvironmentVariable("FLEET_MODS_LIVE") == "1")
            throw new InvalidOperationException($"FLEET_MODS_LIVE=1 but {(bun is null ? "no Bun was found" : "mods/host/dist/host.js isn't built")}.");
        return null;
    }

    /// <summary>A fixture mod's folder (Fixtures/Mods/{name}) in the test output.</summary>
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mods", name);

    /// <summary>Copies a fixture mod to <paramref name="destination"/>, which then holds its mod.json.</summary>
    public static string CopyFixture(string name, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(Fixture(name)))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        return destination;
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The <c>dotnet test</c> row from docs/mods/api.md's worked example, as a ToolUse or ToolResult render.</summary>
    public static JsonElement DotnetTestRow(string component, string sessionId = "ses_test1") => Json($$"""
        {
          "component": "{{component}}", "sessionId": "{{sessionId}}", "requestId": "call_1",
          "props": {
            "tool": "bash", "rawTool": "Bash", "category": "shell", "status": "completed", "title": "dotnet test",
            "input": { "command": "dotnet test" }, "inputTruncated": false,
            "output": "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218", "outputTruncated": false
          }
        }
        """);

    public static JsonElement Site(string component, string sessionId = "ses_test1") => Json($$"""
        { "component": "{{component}}", "sessionId": "{{sessionId}}", "requestId": "{{sessionId}}", "props": {{(component == "ComposerBand" ? """{ "isWorking": false }""" : "{}")}} }
        """);

    private static string? FindBun()
    {
        if (Environment.GetEnvironmentVariable("FLEET_TEST_BUN") is { Length: > 0 } configured && File.Exists(configured))
            return configured;
        var exe = OperatingSystem.IsWindows() ? "bun.exe" : "bun";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var installed = Path.Combine(home, ".bun", "bin", exe);
        if (File.Exists(installed))
            return installed;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, exe);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}

/// <summary>Answers the host's <c>$</c> calls from memory and keeps every notification, for live tests of the transport.</summary>
internal sealed class RecordingModHostCalls : IModHostCalls
{
    public ConcurrentDictionary<string, JsonElement> Store { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<(string Method, JsonElement Params)> Notifications { get; } = new();

    public ConcurrentQueue<(string Method, JsonElement Params)> Requests { get; } = new();

    public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        Requests.Enqueue((method, parameters.Clone()));
        var key = parameters.TryGetProperty("key", out var k) ? $"{parameters.GetProperty("mod").GetString()}/{k.GetString()}" : null;
        switch (method)
        {
            case "store.get":
                return Task.FromResult(Store.TryGetValue(key!, out var value)
                    ? RealModHost.Json($$"""{ "value": {{value.GetRawText()}} }""")
                    : RealModHost.Json("{}"));
            case "store.set":
                Store[key!] = parameters.GetProperty("value").Clone();
                return Task.FromResult(RealModHost.Json("{}"));
            case "session.get":
                return Task.FromResult(RealModHost.Json($$"""
                    { "id": "{{parameters.GetProperty("sessionId").GetString()}}", "title": "Make test output readable", "harness": "opencode", "cwd": "/work/demo", "surfaces": [] }
                    """));
            default:
                throw new ModHostRpcException(ModHostErrorCodes.MethodNotFound, $"method not found: {method}");
        }
    }

    public void HandleNotification(string method, JsonElement parameters) => Notifications.Enqueue((method, parameters.Clone()));

    public IEnumerable<JsonElement> Of(string method) => Notifications.Where(n => n.Method == method).Select(n => n.Params);
}
