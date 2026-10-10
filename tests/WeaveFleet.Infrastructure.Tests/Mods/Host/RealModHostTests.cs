using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
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

    /// <summary>A service whose user has test-chips kept (through the store's Keep, without a check) and Mods on.</summary>
    private async Task<(ModHostService Service, ModHostSupervisor Supervisor)?> StartAsync()
    {
        if (Real() is not { } real)
            return null;
        var draft = _store.DraftFolder(User, "ses_live1", "test-chips");
        Directory.CreateDirectory(draft);
        foreach (var file in Directory.EnumerateFiles(Fixture("test-chips")))
            File.Copy(file, Path.Combine(draft, Path.GetFileName(file)));
        await _store.KeepAsync(User, "ses_live1", "test-chips", new ModKeepSource(null, null), (_, _) => Task.FromResult<JsonElement?>(null));

        _service = new ModHostService(
            new ModHostOptions { RequestTimeout = TimeSpan.FromSeconds(10) },
            _connections,
            new SwitchedOn(),
            new FixedBun(real.Bun),
            new ModHostFiles(AppContext.BaseDirectory, real.Script),
            _store,
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

    private sealed class FixedBun(string path) : IModHostBun
    {
        public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult<BunLocation?>(new BunLocation(path, BunSources.Configured, "1.4.2"));
    }
}
