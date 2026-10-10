using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Users;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>Every user's mod host (<see cref="IModHost"/>), started when Fleet starts.</summary>
public sealed class ModHostService : IModHost, IHostedService, IAsyncDisposable
{
    /// <summary>The user Fleet runs as without sign-in (<c>LocalUserContext</c>): their host is brought up at start-up.</summary>
    public const string LocalUserId = "local-user";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    public ModHostService(
        IModHostConnectionFactory connections,
        IModUserGate gate,
        IModStrikeRecorder strikes,
        IModHostUi ui,
        IModHostSignals signals,
        IModHostBun bun,
        IModHostFiles files,
        IModDraftWatcher watcher,
        IModVersionStore store,
        IServiceScopeFactory scopes,
        IBackgroundUserScope users,
        IEventBroadcaster events,
        ModLogBook log,
        TimeProvider time,
        ILogger<ModHostService> logger)
        : this(new ModHostDependencies(connections, gate, strikes, ui, signals, bun, files, watcher, store, (_, _, _) => throw new NotImplementedException(), log, time, logger), events, [LocalUserId])
    {
        _ = scopes;
        _ = users;
    }

    internal ModHostService(ModHostDependencies deps, IEventBroadcaster events, IReadOnlyList<string> startupUsers)
    {
        _ = deps;
        _ = events;
        _ = startupUsers;
    }

    internal ModHostSupervisor? SupervisorOf(string userId) => throw new NotImplementedException();

    public ModHostStatus GetStatus(string userId) => throw new NotImplementedException();

    public Task EnsureAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<JsonElement> CheckAsync(string userId, string folder, CancellationToken ct = default) => throw new NotImplementedException();

    public Task<ModDispatchResult> DispatchAsync(string userId, ModDispatchRequest request, CancellationToken ct = default) => throw new NotImplementedException();

    public Task ForgetSessionAsync(string userId, string sessionId, CancellationToken ct = default) => throw new NotImplementedException();

    public ModLoadProblem? GetLoadProblem(string userId, string modId) => throw new NotImplementedException();

    public IReadOnlyList<ModLogLine> GetLog(string userId, string modId) => throw new NotImplementedException();

    public Task StartAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task StopAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
