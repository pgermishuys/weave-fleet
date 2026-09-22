using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Infrastructure.Harnesses.OpenCode2;

/// <summary>
/// Which server a session runs on: one per owner, and one more per profile version the owner's sessions use. V2 reads
/// its config per folder and takes a profile only from its environment, so two sessions in one folder with different
/// profiles can't share a server.
/// </summary>
internal readonly record struct OpenCode2ServerKey(string OwnerUserId, string? ProfileHash)
{
    public static OpenCode2ServerKey For(string ownerUserId, OpenCode2Profile? profile) => new(ownerUserId, profile?.Hash);

    public override string ToString() => ProfileHash is null ? OwnerUserId : $"{OwnerUserId}, profile {ProfileHash}";
}

/// <summary>
/// The V2 servers Fleet runs, by <see cref="OpenCode2ServerKey"/>. A server is started on first use and stays up; one
/// started with other settings is replaced once nothing runs on it: no turn and no background shell
/// (<see cref="OpenCode2Server.IsIdleAsync"/>). A profile's server also stops once it's gone
/// <see cref="ProfileIdleTimeout"/> without use and nothing runs on it (<see cref="StopIdleAsync"/>):
/// each is a process of its own, with inotify watches on the user's home, and a session that needs it again starts it.
/// The owner's server without a profile stays up, as before profiles.
/// </summary>
internal sealed partial class OpenCode2Servers(
    Func<OpenCode2ServerKey, OpenCode2ServerSetup, CancellationToken, Task<OpenCode2Server>> start,
    TimeSpan profileIdleTimeout,
    ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<OpenCode2ServerKey, OpenCode2Server> _servers = new();

    // Held while a server starts or stops, so a key never gets two; reads (bridge tokens) don't take it.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public TimeSpan ProfileIdleTimeout => profileIdleTimeout;

    public ICollection<OpenCode2Server> All => _servers.Values;

    /// <summary>
    /// The running server for <paramref name="key"/>, started when there's none or the last one stopped. A server started
    /// with other settings is replaced once nothing runs on it; until then the key keeps using it.
    /// </summary>
    public async Task<OpenCode2Server> GetAsync(OpenCode2ServerKey key, OpenCode2ServerSetup setup, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_servers.TryGetValue(key, out var existing))
            {
                if (existing.IsRunning
                    && ((existing.Setup == setup && !existing.IsOutdated) || !await existing.IsIdleAsync(ct).ConfigureAwait(false)))
                {
                    existing.Touch();
                    return existing;
                }

                if (existing.IsRunning)
                    LogReplacingServer(logger, existing.ProcessId ?? 0, key);
                _servers.TryRemove(key, out _);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var server = await start(key, setup, ct).ConfigureAwait(false);
            _servers[key] = server;
            return server;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// The running server Fleet session <paramref name="fleetSessionId"/> listens on, if any. A delegated child's V2
    /// session lives on its parent's server, and only that server sends its events.
    /// </summary>
    public OpenCode2Server? FindServing(string ownerUserId, string fleetSessionId)
        => _servers.Values.FirstOrDefault(server => server.IsRunning
            && string.Equals(server.OwnerUserId, ownerUserId, StringComparison.Ordinal)
            && server.Serves(fleetSessionId));

    /// <summary>
    /// Stops each profile's server that nobody has used for <see cref="ProfileIdleTimeout"/> and that has nothing
    /// running. Its sessions stay attached to nothing until their next request, which starts it again. Returns how many
    /// stopped.
    /// </summary>
    public async Task<int> StopIdleAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed)
            return 0;

        var stopped = 0;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (key, server) in _servers)
            {
                if (key.ProfileHash is null)
                    continue;

                if (server.IsRunning)
                {
                    if (now - server.LastUsed < profileIdleTimeout || !await server.IsIdleAsync(ct).ConfigureAwait(false))
                        continue;

                    // A request that came in while V2 was asked goes on using it.
                    if (now - server.LastUsed < profileIdleTimeout)
                        continue;

                    LogStoppingIdle(logger, server.ProcessId ?? 0, key, (now - server.LastUsed).TotalMinutes);
                }

                _servers.TryRemove(key, out _);
                await server.DisposeAsync().ConfigureAwait(false);
                stopped++;
            }
        }
        finally
        {
            _lock.Release();
        }

        return stopped;
    }

    /// <summary>Idle servers stop now and the next request starts one on the new binary; a busy one is replaced once it's idle.</summary>
    public async Task AfterUpdateAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (key, server) in _servers)
            {
                if (server.IsRunning && !await server.IsIdleAsync(ct).ConfigureAwait(false))
                {
                    server.MarkOutdated();
                    continue;
                }
                _servers.TryRemove(key, out _);
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var server in _servers.Values)
                await server.DisposeAsync().ConfigureAwait(false);
            _servers.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} ({Key}) started with other settings (built-in skills, messages between sessions) and is idle; replacing it")]
    private static partial void LogReplacingServer(ILogger logger, int processId, OpenCode2ServerKey key);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenCode 2 server {ProcessId} ({Key}) unused for {Minutes:0.#} min with nothing running; stopping it until a session needs it")]
    private static partial void LogStoppingIdle(ILogger logger, int processId, OpenCode2ServerKey key, double minutes);
}
