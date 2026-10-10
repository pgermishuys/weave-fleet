using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Finds one of the user's sessions, as that user; null when they have no such session.</summary>
internal delegate Task<Session?> ModSessionLookup(string userId, string sessionId, CancellationToken ct);

/// <summary>What every user's supervisor shares.</summary>
internal sealed record ModHostDependencies(
    IModHostConnectionFactory Connections,
    IModUserGate Gate,
    IModStrikeRecorder StrikeRecorder,
    IModHostUi Ui,
    IModHostSignals Signals,
    IModHostBun Bun,
    IModHostFiles Files,
    IModDraftWatcher Watcher,
    IModVersionStore Store,
    ModSessionLookup FindSession,
    ModLogBook Log,
    TimeProvider Time,
    ILogger Logger);

/// <summary>One user's mod host: starts and stops it, keeps it in step with the store, runs events through it.</summary>
internal sealed class ModHostSupervisor : IModHostCalls
{
    public static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CheckLease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);

    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30),
    ];

    public ModHostSupervisor(string userId, ModHostDependencies deps)
    {
        UserId = userId;
        _ = deps;
    }

    public string UserId { get; }

    public ModHostStatus GetStatus() => throw new NotImplementedException();

    public Task EnsureAsync(CancellationToken ct = default) => throw new NotImplementedException();

    public void ForgetRefusals() => throw new NotImplementedException();

    public Task<JsonElement> CheckAsync(string folder, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<ModDispatchResult> DispatchAsync(ModDispatchRequest request, CancellationToken ct = default) => throw new NotImplementedException();

    public Task ForgetSessionAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();

    public ModLoadProblem? GetLoadProblem(string modId) => throw new NotImplementedException();

    internal int StrikesOf(string modId) => throw new NotImplementedException();

    public Task ShutdownAsync() => throw new NotImplementedException();

    public Task<JsonElement> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct) => throw new NotImplementedException();

    public void HandleNotification(string method, JsonElement parameters) => throw new NotImplementedException();
}
