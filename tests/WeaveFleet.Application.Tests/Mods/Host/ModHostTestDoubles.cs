using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Runtimes;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Mods.Host;

internal static class Guard
{
    /// <summary>Every await in these tests has a deadline, so a deadlock fails fast instead of hanging the run.</summary>
    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    public static Task Within(this Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));
}

internal sealed class FakeConnection(int processId) : IModHostConnection
{
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProcessId => processId;
    public ModHostInitializeResult Host { get; } = new(1, "host-1", "bun-1");
    public Task<int> Exited => _exited.Task;
    public Func<Task<JsonElement>> OnRequest { get; set; } = () => Task.FromResult(default(JsonElement));

    /// <summary>When false, <see cref="Kill"/> leaves the process "running", to count how often Fleet kills it.</summary>
    public bool KillEndsProcess { get; set; } = true;
    public List<TimeSpan> Shutdowns { get; } = [];
    public int Kills { get; private set; }
    public int Disposals { get; private set; }

    public void Crash(int code = 1) => _exited.TrySetResult(code);

    public Task<JsonElement> RequestAsync(string method, JsonElement parameters, TimeSpan timeout) => OnRequest();

    public Task ShutdownAsync(TimeSpan grace)
    {
        Shutdowns.Add(grace);
        _exited.TrySetResult(0);
        return Task.CompletedTask;
    }

    public void Kill()
    {
        Kills++;
        if (KillEndsProcess)
            _exited.TrySetResult(137);
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeFactory : IModHostConnectionFactory
{
    private readonly Channel<FakeConnection> _started = Channel.CreateUnbounded<FakeConnection>();

    public List<ModHostLaunch> Launches { get; } = [];
    public List<FakeConnection> Connections { get; } = [];
    public Exception? Fail { get; set; }

    /// <summary>When set, a start waits for it, so a test can hold the host in <c>starting</c>.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completes with the next host started (ones started earlier and not yet read count).</summary>
    public Task<FakeConnection> NextStart() => _started.Reader.ReadAsync().AsTask().Within();

    public async Task<IModHostConnection> StartAsync(ModHostLaunch launch, IModHostCalls calls, CancellationToken ct)
    {
        Launches.Add(launch);
        if (Fail is { } fail)
            throw fail;
        if (Hold is { } hold)
            await hold.Task.ConfigureAwait(false);
        var connection = new FakeConnection(100 + Connections.Count);
        Connections.Add(connection);
        _started.Writer.TryWrite(connection);
        return connection;
    }
}

internal sealed class FakeGate : IModUserGate
{
    public bool On { get; set; } = true;
    public bool Safe { get; set; }

    public Task<bool> IsSwitchedOnAsync(string userId, CancellationToken ct) => Task.FromResult(On);

    public bool IsSafeMode(string userId) => Safe;
}

internal sealed class FakeBun : IModHostBun
{
    public BunLocation? Location { get; set; } = new("/bun/bin/bun", "test", "1.3.0");

    public Task<BunLocation?> FindAsync(CancellationToken ct) => Task.FromResult(Location);
}

internal sealed class FakeFiles : IModHostFiles
{
    public string? HostScript { get; set; } = "/app/mods-host/host.js";
    public string FleetVersion => "9.9.9";
}

/// <summary>Hands out the events a test publishes, as a subscription to the "sessions" topic would.</summary>
internal sealed class ChannelBroadcaster : IEventBroadcaster
{
    private readonly Channel<BroadcastEvent> _events = Channel.CreateUnbounded<BroadcastEvent>();

    public void PublishModsChanged(string userId)
        => _events.Writer.TryWrite(new BroadcastEvent("sessions", EventTypes.ModsChanged, default, DateTimeOffset.UnixEpoch, UserId: userId));

    public async IAsyncEnumerable<BroadcastEvent> SubscribeAsync(IReadOnlyList<string> topics, string? subscriberUserId, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return e;
    }

    public Task BroadcastAsync(string topic, string type, JsonElement payload, string? userId, CancellationToken ct) => throw new NotSupportedException();
    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, string? userId, CancellationToken ct) => throw new NotSupportedException();
    public Task BroadcastAsync(string topic, string type, JsonElement payload, DomainEvent? domainEvent, string? userId, CancellationToken ct) => throw new NotSupportedException();
    public Task BroadcastAsync(string topic, string type, JsonElement payload, long? eventId, DomainEvent? domainEvent, string? userId, CancellationToken ct) => throw new NotSupportedException();
}
