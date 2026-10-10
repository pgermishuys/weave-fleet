using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Mods;
using WeaveFleet.Infrastructure.Mods.Host;
using WeaveFleet.Infrastructure.Users;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Mods.Host;

/// <summary>
/// The whole of Fleet's side, the supervisor with the real store, draft watcher and transport, against the real host:
/// Keep with the host's check, loading, a draft reloading on save, a crash and restart, a hang and the 15 s restart,
/// a check in safe mode, and shutdown.
/// </summary>
[Trait("Category", RealModHost.Category)]
public sealed class RealModHostServiceTests : IAsyncDisposable
{
    private const string User = "user-live-1";
    private const string Session = "ses_live1";

    private readonly string _root = Directory.CreateTempSubdirectory("fleet-realhost-svc-").FullName;
    private readonly FileModVersionStore _store;
    private readonly InMemorySessionRepository _sessions = new();
    private readonly LiveGate _gate = new();
    private ModHostService? _service;

    public RealModHostServiceTests()
    {
        _store = new FileModVersionStore(Path.Combine(_root, "mods"));
        _sessions.Seed(new Session { Id = Session, UserId = User, Title = "Make test output readable", HarnessType = "opencode", Directory = "/work/demo" });
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            await _service.DisposeAsync();
        }

        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private ModHostService? Start()
    {
        if (RealModHost.Find() is not { } real)
            return null;
        var scopes = TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionRepository>(_sessions));
        _service = new ModHostService(
            new ModHostConnectionFactory(NullLogger<ModHostConnectionFactory>.Instance),
            _gate,
            new StoreStrikes(_store),
            new LoggingModHostUi(NullLogger<LoggingModHostUi>.Instance),
            new NoModHostSignals(),
            new FixedBun(real.Bun),
            new ModHostFiles(AppContext.BaseDirectory, real.HostScript),
            new FileModDraftWatcher(TimeProvider.System, NullLogger<FileModDraftWatcher>.Instance),
            _store,
            scopes,
            new BackgroundUserScope(),
            new FakeEventBroadcaster(),
            new ModLogBook(),
            TimeProvider.System,
            NullLogger<ModHostService>.Instance);
        return _service;
    }

    private string Draft(string fixture, string sessionId = Session)
        => RealModHost.CopyFixture(fixture, _store.DraftFolder(User, sessionId, fixture));

    private static Task<ModDispatchResult> RenderAsync(ModHostService host, string component, string sessionId = Session)
        => host.DispatchAsync(User, new ModDispatchRequest("ui.render", sessionId, component is "ToolUse" or "ToolResult" ? RealModHost.DotnetTestRow(component, sessionId) : RealModHost.Site(component, sessionId)));

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value) || clock.Elapsed > within)
                return value;
            await Task.Delay(100);
        }
    }

    private static string Text(ModDispatchResult result) => result.Result is { } tree ? tree.GetRawText() : "";

    [Fact]
    public async Task Keep_runs_the_hosts_check_on_its_staged_copy_and_the_kept_version_draws_the_chips()
    {
        if (Start() is not { } host)
            return;
        Draft("test-chips");

        var version = await _store.KeepAsync(User, Session, "test-chips", new ModKeepSource("Make test output readable", null),
            (staged, ct) => CheckAsync(host, staged, ct));

        version.Check.ShouldNotBeNull();
        version.Check.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        version.Check.Value.GetProperty("lines").GetInt32().ShouldBe(47);

        await host.EnsureAsync(User);
        var status = host.GetStatus(User);
        status.State.ShouldBe(ModHostStates.Running);
        status.Loaded.ShouldBe(["test-chips@v1"]);
        status.BunPath.ShouldNotBeNull();

        var chips = await RenderAsync(host, "ToolUse");
        chips.Dispatched.ShouldBeTrue();
        Text(chips).ShouldContain("212 passed");
        chips.DrawnBy.ShouldBe(["test-chips@v1"]);

        // A read row isn't test-chips' business: nothing crosses the pipe.
        var read = await host.DispatchAsync(User, new ModDispatchRequest("ui.render", Session, RealModHost.Json(
            """{ "component": "ToolUse", "sessionId": "ses_live1", "requestId": "call_2", "props": { "tool": "read", "rawTool": "Read", "category": "read", "status": "completed", "title": "README.md", "input": {}, "inputTruncated": false, "output": "", "outputTruncated": false } }""")));
        read.Dispatched.ShouldBeFalse();
    }

    private static async Task<JsonElement?> CheckAsync(ModHostService host, string staged, CancellationToken ct)
    {
        Path.GetFileName(staged).ShouldBe("test-chips");
        return await host.CheckAsync(User, staged, ct);
    }

    [Fact]
    public async Task Saving_a_draft_reloads_it_from_a_new_staged_copy()
    {
        if (Start() is not { } host)
            return;
        var draft = Draft("band-draft");

        await host.EnsureAsync(User);
        host.GetStatus(User).Loaded.ShouldBe([$"band-draft@draft:{Session}"]);
        Text(await RenderAsync(host, "ComposerBand")).ShouldContain("first save");

        File.WriteAllText(Path.Combine(draft, "mod.ts"), File.ReadAllText(Path.Combine(draft, "mod.ts")).Replace("first save", "second save"));

        var after = await EventuallyAsync(() => RenderAsync(host, "ComposerBand"), r => Text(r).Contains("second save"), TimeSpan.FromSeconds(10));
        Text(after).ShouldContain("second save");

        // Draft scope A: another session never gets the draft's band.
        (await RenderAsync(host, "ComposerBand", "ses_live2")).Dispatched.ShouldBeFalse();
    }

    [Fact]
    public async Task A_host_that_dies_is_restarted_and_its_sessions_start_again_with_reload_before_anything_else()
    {
        if (Start() is not { } host)
            return;
        Draft("probe-mod");
        await _store.KeepAsync(User, Session, "probe-mod", new ModKeepSource(null, null), async (staged, ct) => await host.CheckAsync(User, staged, ct));
        await host.EnsureAsync(User);

        Text(await RenderAsync(host, "StatusChip")).ShouldContain("1 starts in Make test output readable");
        var first = host.GetStatus(User);

        using (var process = Process.GetProcessById(first.ProcessId!.Value))
            process.Kill();

        var restarted = await EventuallyAsync(() => Task.FromResult(host.GetStatus(User)),
            s => s.State == ModHostStates.Running && s.ProcessId != first.ProcessId, TimeSpan.FromSeconds(10));
        restarted.State.ShouldBe(ModHostStates.Running);
        restarted.Restarts.ShouldBe(1);
        restarted.LastExit.ShouldNotBeNull();
        restarted.Loaded.ShouldBe(["probe-mod@v1"]);

        // The reload start ran first (it counted a second start into $.store); no implicit "start" ever ran after the crash.
        Text(await RenderAsync(host, "StatusChip")).ShouldContain("2 starts in Make test output readable");
        var log = host.GetLog(User, "probe-mod@v1").Select(l => l.Text).ToList();
        log.Where(t => t.StartsWith("start ", StringComparison.Ordinal)).ShouldBe(["start start", "start reload"]);
    }

    [Fact]
    public async Task A_hook_that_never_yields_is_restarted_after_15_seconds_and_struck()
    {
        if (Start() is not { } host)
            return;
        Draft("hang-hook");
        await host.EnsureAsync(User);
        var first = host.GetStatus(User);
        first.Loaded.ShouldBe([$"hang-hook@draft:{Session}"]);

        var clock = Stopwatch.StartNew();
        var hung = await RenderAsync(host, "ComposerBand");

        hung.Dispatched.ShouldBeFalse();
        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(14.5));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));

        var restarted = await EventuallyAsync(() => Task.FromResult(host.GetStatus(User)),
            s => s.State == ModHostStates.Running && s.ProcessId != first.ProcessId, TimeSpan.FromSeconds(10));
        restarted.Restarts.ShouldBe(1);
        restarted.LastExit!.ExitCode.ShouldBeNull();
        host.GetLog(User, $"hang-hook@draft:{Session}").ShouldContain(l => l.Text.Contains("15 s"));
    }

    [Fact]
    public async Task A_host_started_for_a_check_in_safe_mode_loads_no_mod()
    {
        if (Start() is not { } host)
            return;
        Draft("band-draft");
        _gate.SafeMode = true;

        var report = await _store.CheckDraftAsync(User, Session, "band-draft", async (staged, ct) => await host.CheckAsync(User, staged, ct));

        report!.Value.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var status = host.GetStatus(User);
        status.State.ShouldBe(ModHostStates.Running);
        status.Loaded.ShouldBeEmpty();
        (await RenderAsync(host, "ComposerBand")).Dispatched.ShouldBeFalse();
    }

    [Fact]
    public async Task Stopping_Fleet_shuts_the_host_down()
    {
        if (Start() is not { } host)
            return;
        Draft("band-draft");
        await host.EnsureAsync(User);
        var pid = host.GetStatus(User).ProcessId!.Value;

        var clock = Stopwatch.StartNew();
        await host.StopAsync(CancellationToken.None);

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        IsGone(pid).ShouldBeTrue();
        Directory.Exists(Path.Combine(_store.HostFolder(User), "drafts")).ShouldBeFalse();
    }

    private static bool IsGone(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class LiveGate : IModUserGate
    {
        public bool SafeMode { get; set; }

        public Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct) => Task.FromResult(true);

        public bool IsSafeMode(string userId) => SafeMode;
    }

    private sealed class FixedBun(string path) : IModHostBun
    {
        public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult<BunLocation?>(new BunLocation(path, BunSources.Configured, null));

        public Task PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StoreStrikes(IModVersionStore store) : IModStrikeRecorder
    {
        public Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct)
            => store.SetOffAsync(userId, name, new ModOff(ModOffBy.Strikes, DateTimeOffset.UtcNow, message), ct);

        public Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct)
            => store.SetDraftOffAsync(userId, sessionId, name, new ModOff(ModOffBy.Strikes, DateTimeOffset.UtcNow, message), ct);
    }
}
