using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Application.Users;
using WeaveFleet.Infrastructure.Mods;
using WeaveFleet.Infrastructure.Mods.Host;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// The supervisor and the transport against the real host: <c>mods/host/dist/host.js</c> on Bun (<c>FLEET_TEST_BUN</c>,
/// <c>~/.bun/bin/bun</c> or <c>bun</c> on PATH). Without them these return early, unless <c>FLEET_MODS_LIVE=1</c> (CI's
/// mod host job), where a missing Bun or host.js fails them.
/// </summary>
[Trait("Category", "ModHostLive")]
public sealed class RealModHostTests : IAsyncDisposable
{
    private const string User = "user-live-1";

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-realhost-").FullName;
    private readonly FileModVersionStore _store;
    private readonly LoggingUi _ui = new();
    private readonly RecordingFactory _connections = new(new ModHostConnectionFactory(NullLogger<ModHostConnectionFactory>.Instance));
    private ModHostService? _service;

    public RealModHostTests() => _store = new FileModVersionStore(Path.Combine(_root, "mods"));

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
            await _service.DisposeAsync();
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static (string Bun, string Script)? Real()
    {
        var script = new ModHostFiles(AppContext.BaseDirectory).HostScript;
        var bun = new[]
            {
                Environment.GetEnvironmentVariable("FLEET_TEST_BUN"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bun", "bin", OperatingSystem.IsWindows() ? "bun.exe" : "bun"),
            }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, OperatingSystem.IsWindows() ? "bun.exe" : "bun")))
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (bun is not null && script is not null)
            return (bun, script);
        if (Environment.GetEnvironmentVariable("FLEET_MODS_LIVE") == "1")
            throw new InvalidOperationException($"FLEET_MODS_LIVE=1 but {(bun is null ? "no Bun was found" : "mods/host/dist/host.js isn't built")}.");
        return null;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mods", name);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A service whose user has test-chips (and <paramref name="also"/>) kept, through the store's Keep without a check, and Mods on.</summary>
    private async Task<(ModHostService Service, ModHostSupervisor Supervisor)?> StartAsync(params string[] also)
    {
        if (Real() is not { } real)
            return null;
        foreach (var name in also.Prepend("test-chips"))
        {
            var draft = _store.DraftFolder(User, "ses_live1", name);
            Directory.CreateDirectory(draft);
            foreach (var file in Directory.EnumerateFiles(Fixture(name)))
                File.Copy(file, Path.Combine(draft, Path.GetFileName(file)));
            await _store.KeepAsync(User, "ses_live1", name, new ModKeepSource(null, null), (_, _) => Task.FromResult<JsonElement?>(null));
        }

        return await LaunchAsync(real);
    }

    private async Task<(ModHostService Service, ModHostSupervisor Supervisor)> LaunchAsync((string Bun, string Script) real)
    {
        _service = new ModHostService(
            new ModHostOptions { RequestTimeout = TimeSpan.FromSeconds(10) },
            _connections,
            new SwitchedOn(),
            new FixedBun(real.Bun),
            new ModHostFiles(AppContext.BaseDirectory, real.Script),
            _store,
            new NoStrikes(),
            _ui,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new NoUserScope(),
            new FakeEventBroadcaster(),
            TimeProvider.System,
            NullLogger<ModHostService>.Instance);
        await _service.EnsureAsync(User).WaitAsync(TimeSpan.FromSeconds(30));
        return (_service, _service.SupervisorOf(User)!);
    }

    /// <summary>Completes with the first status, from now on, that <paramref name="done"/> accepts.</summary>
    private static Task<ModHostStatus> WhenAsync(ModHostSupervisor supervisor, Func<ModHostStatus, bool> done)
    {
        var reached = new TaskCompletionSource<ModHostStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.Changed += status =>
        {
            if (done(status))
                reached.TrySetResult(status);
        };
        return reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_kept_mod_starts_the_host_with_an_empty_environment_in_the_users_host_folder()
    {
        if (await StartAsync() is not (var service, _))
            return;

        var status = service.GetStatus(User);
        status.State.ShouldBe(ModHostStates.Running);
        status.HostVersion.ShouldNotBeNullOrWhiteSpace();
        if (OperatingSystem.IsLinux())
        {
            File.ReadAllText($"/proc/{status.ProcessId}/environ").ShouldBeEmpty();
            new DirectoryInfo($"/proc/{status.ProcessId}/cwd").ResolveLinkTarget(true)!.FullName.ShouldBe(_store.HostFolder(User));
        }
    }

    [Fact]
    public async Task A_host_that_dies_is_started_again()
    {
        if (await StartAsync() is not (var service, var supervisor))
            return;
        var first = service.GetStatus(User);
        var restarted = WhenAsync(supervisor, s => s.State == ModHostStates.Running && s.ProcessId != first.ProcessId);

        using (var process = Process.GetProcessById(first.ProcessId!.Value))
            process.Kill();

        (await restarted).Restarts.ShouldBe(1);
    }

    [Fact]
    public async Task A_host_busy_in_a_loop_is_killed_at_the_request_timeout_and_started_again()
    {
        if (await StartAsync() is not (var service, var supervisor))
            return;
        var first = service.GetStatus(User);
        await supervisor.RequestAsync("load", Json($$"""{ "id": "hang-hook@v1", "name": "hang-hook", "version": 1, "root": {{JsonSerializer.Serialize(Fixture("hang-hook"))}} }"""));
        var restarted = WhenAsync(supervisor, s => s.State == ModHostStates.Running && s.ProcessId != first.ProcessId);

        var hang = await Should.ThrowAsync<ModHostNotReadyException>(() => supervisor.RequestAsync("dispatch", Json("""
            { "event": "ui.render", "sessionId": "ses_live1", "mods": ["hang-hook@v1"],
              "e": { "component": "ComposerBand", "sessionId": "ses_live1", "requestId": "ses_live1", "props": { "isWorking": false } } }
            """)));

        hang.Message.ShouldContain("restarting");
        (await restarted).Restarts.ShouldBe(1);
    }

    [Fact]
    public async Task A_kept_mod_loads_and_draws_a_dotnet_test_row_and_an_unhooked_event_never_crosses()
    {
        if (await StartAsync() is not (var service, _))
            return;
        service.GetLoadProblem(User, "test-chips@v1").ShouldBeNull();
        var e = Json("""{ "sessionId": "ses_live1", "component": "ToolUse", "props": { "tool": "bash", "input": { "command": "dotnet test" }, "output": "Failed!  - Failed: 2, Passed: 212, Skipped: 4, Total: 218" } }""");
        var drawn = await service.DispatchAsync(User, new ModDispatchRequest("ui.render", "ses_live1", e)).WaitAsync(TimeSpan.FromSeconds(10));
        drawn.DrawnBy.ShouldBe(["test-chips@v1"]);
        drawn.Result!.Value.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("props").GetProperty("label").GetString()).ShouldBe(["212 passed", "2 failed", "4 skipped"]);
        (await service.DispatchAsync(User, new ModDispatchRequest("turn.complete", "ses_live1", e)).WaitAsync(TimeSpan.FromSeconds(10))).Dispatched.ShouldBeFalse();
    }

    [Fact]
    public async Task A_prompt_submit_hook_rewrites_a_prompt_and_adds_context_through_the_real_host()
    {
        if (Real() is not { } real)
            return;
        var folder = Directory.CreateDirectory(Path.Combine(_root, "shout")).FullName;
        File.WriteAllText(Path.Combine(folder, "mod.json"), """{"name":"shout","version":"0.1.0","description":"Shouts a prompt","hooks":"./mod.ts"}""");
        File.WriteAllText(Path.Combine(folder, "mod.ts"), """
            import type { Register } from "fleet-mods";
            export const register: Register = (on) => {
              on("prompt.submit", { text: /^please/i }, ($, e, next) => next({ ...e, text: e.text.toUpperCase(), context: [...e.context, "Shouted."] }));
            };
            """);
        await _store.KeepFolderAsync(User, "shout", folder, new ModKeepSource(null, null), (_, _) => Task.FromResult<JsonElement?>(null));
        var (service, _) = await LaunchAsync(real);
        var hook = new ModPromptHook(service, TimeProvider.System, NullLogger<ModPromptHook>.Instance);

        var shouted = await hook.ApplyAsync(User, "ses_live1", "please look at the logs", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var untouched = await hook.ApplyAsync(User, "ses_live1", "look at the logs", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        shouted.Text.ShouldBe("PLEASE LOOK AT THE LOGS");
        shouted.Context.ShouldBe(["Shouted."]);
        untouched.Text.ShouldBe("look at the logs", "the host applied the matcher");
        untouched.Context.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_shipped_sample_is_kept_checked_and_draws_pills_for_a_dotnet_test_row_through_the_render_service()
    {
        if (Real() is not { } real || new ModHostFiles(AppContext.BaseDirectory).SamplesFolder is not { } samples)
            return;
        var (service, _) = await LaunchAsync(real);

        // The real store and the real host's check, as "Add" runs them.
        var version = await _store.KeepFolderAsync(User, "test-chips", Path.Combine(samples, "test-chips"), new ModKeepSource(null, "Fleet's sample"),
            async (staged, ct) => await service.CheckAsync(User, staged, ct));
        (version.Number, version.SessionId, version.Check!.Value.GetProperty("ok").GetBoolean()).ShouldBe((1, null, true));
        await service.EnsureAsync(User).WaitAsync(TimeSpan.FromSeconds(30));
        var render = new ModRenderService(service, new TestUserContext(User));
        render.SitesFor("ses_live1").Sites.Select(s => $"{s.Site}={string.Join(",", s.Tools!)}").ShouldBe(["ToolUse=bash,shell", "ToolResult=bash,shell"]);

        const string Output = "Failed WidgetTests.Spins [4 ms]\nFailed!  - Failed: 1, Passed: 3, Skipped: 0, Total: 4, Duration: 1 s";
        var props = JsonSerializer.SerializeToElement(new { tool = "bash", input = new { command = "dotnet test" }, output = Output });
        var use = (await render.RenderAsync("ses_live1", new ModRenderRequest("ToolUse", "call_1", props))).Value;

        use.Mods.ShouldBe([new ModRenderedBy("test-chips", false)]);
        use.Tree!.Value.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("props").GetProperty("label").GetString()).ShouldBe(["3 passed", "1 failed"]);
        var opened = (await render.RenderAsync("ses_live1", new ModRenderRequest("ToolResult", "call_1", props))).Value;
        opened.Tree!.Value.GetRawText().ShouldContain("WidgetTests.Spins");
        var read = JsonSerializer.SerializeToElement(new { tool = "read", input = new { }, output = "" });
        (await render.RenderAsync("ses_live1", new ModRenderRequest("ToolUse", "call_2", read))).Value.Tree.ShouldBeNull();
    }

    [Fact]
    public async Task A_button_a_mod_drew_is_pressed_and_its_callback_runs()
    {
        if (await StartAsync("press-demo") is not (var service, _))
            return;
        var band = Json("""{ "sessionId": "ses_live1", "component": "ComposerBand", "requestId": "ses_live1", "props": { "isWorking": false } }""");
        var drawn = await service.DispatchAsync(User, new ModDispatchRequest("ui.render", "ses_live1", band)).WaitAsync(TimeSpan.FromSeconds(10));
        drawn.DrawnBy.ShouldBe(["press-demo@v1"]);
        var handle = drawn.Result!.Value.GetProperty("handles").GetProperty("onPress").GetString();

        var press = Json($$"""{ "sessionId": "ses_live1", "component": "ComposerBand", "requestId": "ses_live1", "surface": "desktop", "handle": "{{handle}}" }""");
        var pressed = await service.DispatchAsync(User, new ModDispatchRequest("ui.press", "ses_live1", press, "desktop")).WaitAsync(TimeSpan.FromSeconds(10));

        pressed.Dispatched.ShouldBeTrue();
        pressed.Result!.Value.GetProperty("element").GetString().ShouldBe("go");
        (await _ui.Lines.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(("press-demo@v1", "pressed"));
    }

    [Fact]
    public async Task Stopping_Fleet_shuts_the_host_down_on_its_shutdown_request()
    {
        if (await StartAsync() is not (var service, _))
            return;
        var host = _connections.Started.Single();

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Exit code 0: it left on `shutdown`, not on the kill at the end of the grace.
        (await host.Exited.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(0);
    }

    /// <summary>The real factory, remembering every connection it made.</summary>
    private sealed class RecordingFactory(IModHostConnectionFactory inner) : IModHostConnectionFactory
    {
        public List<IModHostConnection> Started { get; } = [];

        public async Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct)
        {
            var connection = await inner.StartAsync(launch, calls, ct);
            Started.Add(connection);
            return connection;
        }
    }

    private sealed class SwitchedOn : IModUserGate
    {
        public Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct) => Task.FromResult(true);

        public bool IsSafeMode(string userId) => false;
    }

    /// <summary>Drops every <c>$.ui</c> call but the first log line, which it hands to the test.</summary>
    private sealed class LoggingUi : IModHostUi
    {
        private readonly NoModHostUi _none = new();

        public TaskCompletionSource<(string Mod, string Text)> Lines { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct) => _none.OpenPaneAsync(userId, modId, sessionId, paneId, title, ct);

        public Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct) => _none.ClosePaneAsync(userId, modId, sessionId, paneId, ct);

        public Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct) => _none.ToastAsync(userId, modId, sessionId, text, timeoutMs, tone, ct);

        public IReadOnlyList<string> SurfacesOf(string userId, string sessionId) => [];

        public void Invalidated(string userId, string modId, string? sessionId)
        {
        }

        public void Loaded(string userId)
        {
        }

        public void Logged(string userId, string modId, string? sessionId, string level, string text) => Lines.TrySetResult((modId, text));
    }

    private sealed class NoStrikes : IModStrikeRecorder
    {
        public Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct) => Task.CompletedTask;

        public Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new CancellationTokenSource();
    }

    private sealed class FixedBun(string path) : IModHostBun
    {
        public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult<BunLocation?>(new BunLocation(path, BunSources.Configured, "1.4.2"));
    }
}
