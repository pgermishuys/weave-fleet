using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>What a <see cref="ModHostSupervisor"/> needs from the rest of Fleet.</summary>
internal sealed record ModHostDependencies(
    ModHostOptions Options,
    IModHostConnectionFactory Connections,
    IModUserGate Gate,
    IModHostBun Bun,
    IModHostFiles Files,
    IModVersionStore Store,
    TimeProvider Time,
    ILogger Logger);

/// <summary>
/// One user's mod host: keeps the process running while a mod of theirs should run, stops it when none should, and starts
/// it again, after a growing wait, when it dies or stops answering. Everything that changes the process runs under one gate.
/// </summary>
#pragma warning disable CA1001 // The gate is never waited on by handle, so there is nothing to release.
internal sealed partial class ModHostSupervisor(string userId, ModHostDependencies deps) : IModHostCalls
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _userKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16].ToLowerInvariant();
    private readonly ILogger _logger = deps.Logger;
    private ModHostStatus _status = ModHostStatus.Stopped;
    private IModHostConnection? _connection;
    private IModHostConnection? _killed;
    private ITimer? _restartTimer;
    private DateTimeOffset _upSince;
    private int _step;
    private int _restarts;
    private bool _shutdown;

    private ModHostOptions Options => deps.Options;

    /// <summary>Raised on every change of <see cref="GetStatus"/>, from whichever thread made it.</summary>
    internal event Action<ModHostStatus>? Changed;

    public ModHostStatus GetStatus() => Volatile.Read(ref _status);

    /// <summary>Brings the host in line with the Mods switch, safe mode and what the user has kept or drafted.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_shutdown)
                await ReconcileAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fleet is stopping: the host gets its grace to exit and nothing starts it again.</summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shutdown = true;
            await StopHostAsync().ConfigureAwait(false);
            Set(ModHostStates.Stopped, "Fleet is stopping.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Sends the host a request. A host that doesn't answer in time is killed and restarted, and the caller told so.</summary>
    internal async Task<JsonElement> RequestAsync(string method, JsonElement parameters)
    {
        var connection = Volatile.Read(ref _connection)
            ?? throw new ModHostNotReadyException(GetStatus().Reason ?? "The mod host isn't running.");
        try
        {
            return await connection.RequestAsync(method, parameters, Options.RequestTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (!ReferenceEquals(Interlocked.Exchange(ref _killed, connection), connection))
            {
                LogRequestTimedOut(_userKey, method);
                connection.Kill();
            }
            throw new ModHostNotReadyException($"The mod host didn't answer within {Options.RequestTimeout.TotalSeconds:0.#} s; Fleet is restarting it.");
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (await WhyUnwantedAsync(ct).ConfigureAwait(false) is { } reason)
        {
            await StopHostAsync().ConfigureAwait(false);
            Set(ModHostStates.Stopped, reason);
            return;
        }
        if (_connection is not null || _restartTimer is not null)
            return;
        if (deps.Files.HostScript is not { } script)
        {
            Set(ModHostStates.NotReady, "Fleet can't find the mod host (mods-host/host.js).");
            return;
        }
        if (await deps.Bun.FindAsync(ct).ConfigureAwait(false) is not { } bun)
        {
            Set(ModHostStates.NotReady, "The mod runtime (Bun) isn't installed yet.");
            return;
        }

        Set(ModHostStates.Starting, null);
        try
        {
            var launch = new ModHostLaunch(bun.ExecutablePath, script, deps.Store.HostFolder(userId), deps.Files.FleetVersion, _userKey);
            var connection = await deps.Connections.StartAsync(launch, this, ct).ConfigureAwait(false);
            _connection = connection;
            _upSince = deps.Time.GetUtcNow();
            Set(ModHostStates.Running, null, connection, bun.ExecutablePath);
            _ = WatchAsync(connection);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not ModHostNotReadyException)
                LogStartFailed(ex, _userKey);
            Set(ModHostStates.NotReady, ex is ModHostNotReadyException ? ex.Message : "The mod host couldn't start.");
        }
    }

    /// <summary>Why no host is wanted (the sentence for the user), or null when one is.</summary>
    private async Task<string?> WhyUnwantedAsync(CancellationToken ct)
    {
        if (!await deps.Gate.IsSwitchedOnAsync(userId, ct).ConfigureAwait(false))
            return "Mods are off.";
        if (deps.Gate.IsSafeMode(userId))
            return "Started without mods.";
        if ((await deps.Store.ListAsync(userId, ct).ConfigureAwait(false)).Any(h => h is { Active: not null, Off: null }))
            return null;
        foreach (var session in await deps.Store.ListDraftSessionsAsync(userId, ct).ConfigureAwait(false))
        {
            if ((await deps.Store.ListDraftsAsync(userId, session, ct).ConfigureAwait(false)).Any(d => d is { Off: null, Manifest: not null }))
                return null;
        }
        return "No mod is kept or drafted.";
    }

    /// <summary>Waits for the process to exit; if Fleet didn't ask it to (it still holds the connection), plans a restart.</summary>
    private async Task WatchAsync(IModHostConnection connection)
    {
        var code = await connection.Exited.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : -1, TaskScheduler.Default).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_connection, connection))
                    return;
                _connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
                if (deps.Time.GetUtcNow() - _upSince >= Options.StableUptime)
                    _step = 0;
                var wait = Options.Backoff[Math.Min(_step++, Options.Backoff.Count - 1)];
                _restarts++;
                LogHostExited(_userKey, code, wait.TotalSeconds);
                _restartTimer = deps.Time.CreateTimer(_ => _ = RestartAsync(), null, wait, Timeout.InfiniteTimeSpan);
                Set(ModHostStates.Restarting, $"The mod host stopped (exit code {code}); restarting in {wait.TotalSeconds:0.#} s.");
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogStartFailed(ex, _userKey);
        }
    }

    private async Task RestartAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _restartTimer?.Dispose();
                _restartTimer = null;
                if (!_shutdown)
                    await ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LogStartFailed(ex, _userKey);
        }
    }

    private async Task StopHostAsync()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        if (Interlocked.Exchange(ref _connection, null) is not { } connection)
            return;
        try
        {
            await connection.ShutdownAsync(Options.ShutdownGrace).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStartFailed(ex, _userKey);
        }
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void Set(string state, string? reason, IModHostConnection? connection = null, string? bunPath = null)
    {
        var status = new ModHostStatus(state, reason, connection?.ProcessId, bunPath, connection?.Host.HostVersion, _restarts);
        Volatile.Write(ref _status, status);
        Changed?.Invoke(status);
    }

    // ── What the host asks of Fleet (loading, dispatch and answers arrive with the next change) ──

    Task<JsonElement> IModHostCalls.HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct)
        => Task.FromException<JsonElement>(new ModHostRpcException(ModHostErrorCodes.MethodNotFound, $"Fleet doesn't answer {method}."));

    void IModHostCalls.HandleNotification(string method, JsonElement parameters)
    {
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} exited with code {Code}; restarting in {Seconds} s")]
    private partial void LogHostExited(string userKey, int code, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} didn't answer {Method} in time; killing it")]
    private partial void LogRequestTimedOut(string userKey, string method);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mod host for {UserKey} failed")]
    private partial void LogStartFailed(Exception ex, string userKey);
}
