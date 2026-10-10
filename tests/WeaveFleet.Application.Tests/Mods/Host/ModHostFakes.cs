using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods.Host;

/// <summary>Starts <see cref="FakeModHostConnection"/>s and keeps one log of what every connection was asked, in order.</summary>
internal sealed class FakeModHostConnectionFactory : IModHostConnectionFactory
{
    private readonly Lock _sync = new();
    private readonly List<FakeModHostConnection> _started = [];
    private readonly List<ModHostLaunch> _launches = [];
    private readonly ConcurrentQueue<string> _log = new();

    /// <summary>Runs before each start: throw to refuse it, or await something to hold it.</summary>
    public Func<ModHostLaunch, Task>? BeforeStart { get; set; }

    /// <summary>What each mod registers, by name; a mod not here registers <see cref="DefaultHooks"/>.</summary>
    public ConcurrentDictionary<string, IReadOnlyList<ModHookSpec>> Hooks { get; } = new(StringComparer.Ordinal);

    public static IReadOnlyList<ModHookSpec> DefaultHooks { get; } = [new("turn.complete", null), new("ui.render", null)];

    /// <summary>Loads the host refuses (-32001), by id, with the message and report.</summary>
    public ConcurrentDictionary<string, (string Message, JsonElement? Report)> Refusals { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before each load answers: throw or await to script it.</summary>
    public Func<FakeModHostConnection, ModLoadParams, Task>? OnLoad { get; set; }

    /// <summary>Answers dispatches; by default the result is <c>null</c> with no failures.</summary>
    public Func<FakeModHostConnection, ModWireDispatch, CancellationToken, Task<ModWireDispatchResult>>? OnDispatch { get; set; }

    public JsonElement CheckReport { get; set; } = ModHostTests.Json("""{ "ok": true, "name": "test-chips" }""");

    public IReadOnlyList<FakeModHostConnection> Started
    {
        get
        {
            lock (_sync)
                return [.. _started];
        }
    }

    public IReadOnlyList<ModHostLaunch> Launches
    {
        get
        {
            lock (_sync)
                return [.. _launches];
        }
    }

    public FakeModHostConnection Current => Started[^1];

    /// <summary>Everything asked of every connection, in order: <c>start</c>, <c>load id</c>, <c>unload id</c>, <c>dispatch event session [a,b]</c>…</summary>
    public IReadOnlyList<string> Log => [.. _log];

    internal void Record(string entry) => _log.Enqueue(entry);

    public async Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct)
    {
        lock (_sync)
            _launches.Add(launch);
        if (BeforeStart is { } before)
            await before(launch);
        FakeModHostConnection connection;
        lock (_sync)
        {
            connection = new FakeModHostConnection(this, launch, calls, 4000 + _started.Count);
            _started.Add(connection);
        }
        Record("start");
        return connection;
    }
}

internal sealed class FakeModHostConnection(FakeModHostConnectionFactory factory, ModHostLaunch launch, IModHostCalls calls, int processId) : IModHostConnection
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<ModLoadParams> _loads = new();
    private readonly ConcurrentQueue<string> _unloads = new();
    private readonly ConcurrentQueue<(ModWireDispatch Dispatch, TimeSpan Timeout)> _dispatches = new();
    private readonly ConcurrentQueue<(string Root, string Manifest)> _checks = new();
    private readonly ConcurrentQueue<string> _forgotten = new();
    private int _kills;

    public ModHostLaunch Launch { get; } = launch;

    public IModHostCalls Calls { get; } = calls;

    public int ProcessId { get; } = processId;

    public ModHostInitializeResult Host { get; } = new(1, "0.1.0-test", "1.3.0");

    public Task<int> Exited => _exited.Task;

    public IReadOnlyList<ModLoadParams> Loads => [.. _loads];

    public IReadOnlyList<string> Unloads => [.. _unloads];

    public IReadOnlyList<ModWireDispatch> Dispatches => [.. _dispatches.Select(d => d.Dispatch)];

    public IReadOnlyList<TimeSpan> DispatchTimeouts => [.. _dispatches.Select(d => d.Timeout)];

    public IReadOnlyList<(string Root, string Manifest)> Checks => [.. _checks];

    public IReadOnlyList<string> Forgotten => [.. _forgotten];

    public TimeSpan? ShutdownGrace { get; private set; }

    public int Kills => _kills;

    public bool Disposed { get; private set; }

    /// <summary>The process exits by itself with <paramref name="code"/>.</summary>
    public void Exit(int code) => _exited.TrySetResult(code);

    private void ThrowIfClosed()
    {
        if (_exited.Task.IsCompleted)
            throw new ModHostClosedException("The mod host exited.");
    }

    public Task<JsonElement> CheckAsync(string root, string manifest, CancellationToken ct)
    {
        ThrowIfClosed();
        _checks.Enqueue((root, manifest));
        factory.Record($"check {root}");
        return Task.FromResult(factory.CheckReport);
    }

    public async Task<ModLoadResult> LoadAsync(ModLoadParams load, CancellationToken ct)
    {
        ThrowIfClosed();
        _loads.Enqueue(load);
        factory.Record($"load {load.Id}");
        if (factory.OnLoad is { } onLoad)
            await onLoad(this, load);
        if (factory.Refusals.TryGetValue(load.Id, out var refusal))
            throw new ModHostRpcException(ModHostErrorCodes.NotLoaded, refusal.Message, refusal.Report);
        var hooks = factory.Hooks.TryGetValue(load.Name, out var registered) ? registered : FakeModHostConnectionFactory.DefaultHooks;
        return new ModLoadResult(ModHostTests.Json($$"""{ "ok": true, "name": "{{load.Name}}" }"""), hooks);
    }

    public Task UnloadAsync(string id, CancellationToken ct)
    {
        ThrowIfClosed();
        _unloads.Enqueue(id);
        factory.Record($"unload {id}");
        return Task.CompletedTask;
    }

    public async Task<ModWireDispatchResult> DispatchAsync(ModWireDispatch dispatch, TimeSpan timeout, CancellationToken ct)
    {
        ThrowIfClosed();
        _dispatches.Enqueue((dispatch, timeout));
        factory.Record($"dispatch {dispatch.Event} {dispatch.SessionId} [{string.Join(",", dispatch.Mods)}]");
        if (factory.OnDispatch is { } onDispatch)
            return await onDispatch(this, dispatch, ct);
        return new ModWireDispatchResult(ModHostTests.Json("null"), null, []);
    }

    public Task ForgetAsync(string sessionId, CancellationToken ct)
    {
        ThrowIfClosed();
        _forgotten.Enqueue(sessionId);
        factory.Record($"forget {sessionId}");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(TimeSpan grace)
    {
        ShutdownGrace = grace;
        factory.Record("shutdown");
        Exit(0);
        return Task.CompletedTask;
    }

    public void Kill()
    {
        Interlocked.Increment(ref _kills);
        factory.Record("kill");
        Exit(137);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeModUserGate : IModUserGate
{
    public bool SwitchedOn { get; set; } = true;

    public bool SafeMode { get; set; }

    public Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct) => Task.FromResult(SwitchedOn);

    public bool IsSafeMode(string userId) => SafeMode;
}

internal sealed class FakeModStrikeRecorder : IModStrikeRecorder
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>Runs inside each record, after it's been noted: what the real <see cref="ModService"/> would do.</summary>
    public Func<string?, string, string, Task>? OnRecord { get; set; }

    /// <summary><c>kept name: message</c> or <c>draft session/name: message</c>, in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    public async Task RecordKeptAsync(string userId, string name, string message, CancellationToken ct)
    {
        _calls.Enqueue($"kept {name}: {message}");
        if (OnRecord is { } onRecord)
            await onRecord(null, name, message);
    }

    public async Task RecordDraftAsync(string userId, string sessionId, string name, string message, CancellationToken ct)
    {
        _calls.Enqueue($"draft {sessionId}/{name}: {message}");
        if (OnRecord is { } onRecord)
            await onRecord(sessionId, name, message);
    }
}

internal sealed class FakeModHostUi : IModHostUi
{
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyList<string> Calls => [.. _calls];

    public List<string> Surfaces { get; } = ["desktop"];

    public Task OpenPaneAsync(string userId, string modId, string sessionId, string paneId, string? title, CancellationToken ct)
    {
        _calls.Enqueue($"open {userId} {modId} {sessionId} {paneId} {title}");
        return Task.CompletedTask;
    }

    public Task ClosePaneAsync(string userId, string modId, string sessionId, string paneId, CancellationToken ct)
    {
        _calls.Enqueue($"close {userId} {modId} {sessionId} {paneId}");
        return Task.CompletedTask;
    }

    public Task ToastAsync(string userId, string modId, string sessionId, string text, int? timeoutMs, string? tone, CancellationToken ct)
    {
        _calls.Enqueue($"toast {userId} {modId} {sessionId} {text} {timeoutMs} {tone}");
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> SurfacesOf(string userId, string sessionId) => Surfaces;
}

internal sealed class FakeModHostSignals : IModHostSignals
{
    private readonly ConcurrentQueue<string> _signals = new();

    public IReadOnlyList<string> Signals => [.. _signals];

    public void Invalidated(string userId, string modId, string? sessionId) => _signals.Enqueue($"invalidated {userId} {modId} {sessionId}");

    public void Restarted(string userId) => _signals.Enqueue($"restarted {userId}");
}

internal sealed class FakeModHostBun : IModHostBun
{
    private readonly ConcurrentQueue<IReadOnlyCollection<string>> _pruned = new();

    public BunLocation? Location { get; set; } = new("/bun/1.3.0/bun", "fleet", "1.3.0");

    public IReadOnlyList<IReadOnlyCollection<string>> Pruned => [.. _pruned];

    public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult(Location);

    public Task PruneAsync(IReadOnlyCollection<string> inUse, CancellationToken ct)
    {
        _pruned.Enqueue([.. inUse]);
        return Task.CompletedTask;
    }
}

internal sealed class FakeModHostFiles : IModHostFiles
{
    public string? HostScript { get; set; } = "/fleet/mods-host/host.js";

    public string FleetVersion { get; set; } = "0.49.0-test";
}

internal sealed class FakeModDraftWatcher : IModDraftWatcher
{
    private readonly Lock _sync = new();
    private readonly List<(string Root, Action<ModDraftChange> Changed, WatchHandle Handle)> _watches = [];

    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_sync)
                return [.. _watches.Select(w => w.Root)];
        }
    }

    /// <summary>Watches not yet disposed.</summary>
    public int Open
    {
        get
        {
            lock (_sync)
                return _watches.Count(w => !w.Handle.Disposed);
        }
    }

    public IDisposable Watch(string draftsRoot, Action<ModDraftChange> changed)
    {
        var handle = new WatchHandle();
        lock (_sync)
            _watches.Add((draftsRoot, changed, handle));
        return handle;
    }

    /// <summary>A draft was saved: every open watch hears it.</summary>
    public void Save(string sessionId, string name)
    {
        List<Action<ModDraftChange>> open;
        lock (_sync)
            open = [.. _watches.Where(w => !w.Handle.Disposed).Select(w => w.Changed)];
        foreach (var changed in open)
            changed(new ModDraftChange(sessionId, name));
    }

    private sealed class WatchHandle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>An <see cref="IEventBroadcaster"/> whose subscribers hear what's broadcast after they subscribed.</summary>
internal sealed class ChannelEventBroadcaster : IEventBroadcaster
{
    private readonly Lock _sync = new();
    private readonly List<Channel<BroadcastEvent>> _subscribers = [];

    public int Subscribers
    {
        get
        {
            lock (_sync)
                return _subscribers.Count;
        }
    }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, string? userId, CancellationToken ct)
        => BroadcastAsync(topic, type, payload, null, null, userId, ct);

    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, string? userId, CancellationToken ct)
        => BroadcastAsync(topic, type, payload, eventId, null, userId, ct);

    public Task BroadcastAsync(string topic, string type, JsonElement payload, DomainEvent? domainEvent, string? userId, CancellationToken ct)
        => BroadcastAsync(topic, type, payload, null, domainEvent, userId, ct);

    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, DomainEvent? domainEvent, string? userId, CancellationToken ct)
    {
        List<Channel<BroadcastEvent>> subscribers;
        lock (_sync)
            subscribers = [.. _subscribers];
        foreach (var subscriber in subscribers)
            subscriber.Writer.TryWrite(new BroadcastEvent(topic, type, payload, DateTimeOffset.UnixEpoch, eventId, userId));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, string? subscriberUserId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<BroadcastEvent>();
        lock (_sync)
            _subscribers.Add(channel);
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct))
            {
                if (topics.Contains(e.Topic) || topics.Contains("*"))
                    yield return e;
            }
        }
        finally
        {
            lock (_sync)
                _subscribers.Remove(channel);
        }
    }
}

/// <summary>Everything a supervisor needs, faked, over a real temporary folder for the staged drafts.</summary>
internal sealed class ModHostRig : IAsyncDisposable
{
    public const string User = "test-user";

    public ModHostRig()
    {
        Root = Path.Combine(Path.GetTempPath(), "fleet-mod-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Store = new InMemoryModVersionStore { HostRoot = Root };
        Deps = new ModHostDependencies(
            Factory, Gate, Recorder, Ui, Signals, Bun, Files, Watcher, Store,
            (_, sessionId, _) => Sessions.GetByIdAsync(sessionId),
            Log, Time, NullLogger.Instance);
    }

    public string Root { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));

    public InMemoryModVersionStore Store { get; }

    public InMemorySessionRepository Sessions { get; } = new();

    public FakeModHostConnectionFactory Factory { get; } = new();

    public FakeModUserGate Gate { get; } = new();

    public FakeModStrikeRecorder Recorder { get; } = new();

    public FakeModHostUi Ui { get; } = new();

    public FakeModHostSignals Signals { get; } = new();

    public FakeModHostBun Bun { get; } = new();

    public FakeModHostFiles Files { get; } = new();

    public FakeModDraftWatcher Watcher { get; } = new();

    public ModLogBook Log { get; } = new();

    public ModHostDependencies Deps { get; }

    public string HostFolder => Store.HostFolder(User);

    public string StagedDrafts => Path.Combine(HostFolder, "drafts");

    private ModHostSupervisor? _supervisor;

    public ModHostSupervisor Supervisor => _supervisor ??= new ModHostSupervisor(User, Deps);

    /// <summary>Keeps <paramref name="name"/> with versions 1..<paramref name="versions"/>, the last (or <paramref name="active"/>) active.</summary>
    public void Keep(string name, int versions = 1, int? active = null, ModOff? off = null, string user = User)
    {
        for (var i = 1; i <= versions; i++)
            Store.SeedVersion(user, name, i);
        Store.SeedHistory(user, new ModHistory(name, active ?? versions, off, [.. Enumerable.Range(1, versions).Select(Version)]));
    }

    public static ModVersion Version(int number)
        => new(number, new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), $"0.{number}.0", "sha", "ses_test1", "Make test output readable", null, null);

    public void Session(string id, string user = User, string retention = "active")
        => Sessions.Seed(new Session { Id = id, UserId = user, Title = $"Title of {id}", HarnessType = "opencode", Directory = $"/work/{id}", RetentionStatus = retention });

    /// <summary>A draft of <paramref name="name"/> in <paramref name="sessionId"/>, whose session exists unless it's already seeded.</summary>
    public void Draft(string sessionId, string name, ModOff? off = null, bool withManifest = true)
    {
        if (Sessions.All.All(s => s.Id != sessionId))
            Session(sessionId);
        Store.SeedDraft(User, sessionId, name, off, withManifest);
    }

    /// <summary>The draft's staged copies under the host folder, oldest first.</summary>
    public IReadOnlyList<string> StagedCopies(string sessionId, string name)
    {
        var sessionFolder = Path.Combine(StagedDrafts, sessionId);
        if (!Directory.Exists(sessionFolder))
            return [];
        return [.. Directory.GetDirectories(sessionFolder)
            .Select(generation => Path.Combine(generation, name))
            .Where(Directory.Exists)
            .OrderBy(path => int.Parse(Path.GetFileName(Path.GetDirectoryName(path)!), System.Globalization.CultureInfo.InvariantCulture))];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_supervisor is not null)
                await _supervisor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>Helpers shared by the host tests.</summary>
internal static class ModHostTests
{
    /// <summary>How long a test waits for anything before it calls it a hang (a deadlock fails fast instead of hanging).</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Patience);

    public static Task Within(this Task task) => task.WaitAsync(Patience);

    /// <summary>Waits, in real time, until <paramref name="condition"/> holds; fails after <see cref="Patience"/>.</summary>
    public static async Task Eventually(Func<bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Patience)
                throw new ShouldAssertException($"Timed out waiting until {because}.");
            await Task.Delay(5);
        }
    }

    /// <summary>Moves fake time on in steps of <paramref name="step"/> until <paramref name="task"/> finishes; fails after <paramref name="most"/>.</summary>
    public static async Task<T> AdvanceUntil<T>(FakeTimeProvider time, Task<T> task, TimeSpan step, TimeSpan most)
    {
        var moved = TimeSpan.Zero;
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (moved > most || watch.Elapsed > Patience)
                throw new ShouldAssertException($"Still waiting after {moved} of fake time.");
            await Task.Delay(10);
            time.Advance(step);
            moved += step;
        }
        return await task;
    }
}
