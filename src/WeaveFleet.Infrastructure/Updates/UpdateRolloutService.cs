using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Machines;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Infrastructure.Updates;

/// <summary>What the rollout needs to know about a machine, from its <c>GET /api/machine</c>.</summary>
public sealed record NodeUpdateInfo(
    string Version,
    string? StagedVersion,
    int Working,
    bool CanRestart,
    string? RestartUnavailableReason,
    UpdateFailure? RolledBack,
    // The rule it follows and whether it serves the web app (a Fleet, not a node); null/true from one too old to say.
    string? Rule = null,
    bool WebApp = true);

public enum NodeApplyOutcome { Accepted, Busy, Refused, Unreachable }

public sealed record NodeApplyResult(NodeApplyOutcome Outcome, string? Reason = null);

/// <summary>How the rollout talks to the other Fleets: with each one's own token, over its API.</summary>
public interface IUpdateNodes
{
    /// <summary>Null when the machine doesn't answer.</summary>
    Task<NodeUpdateInfo?> ReadAsync(RemoteMachine machine, CancellationToken ct);

    Task<NodeApplyResult> ApplyAsync(RemoteMachine machine, CancellationToken ct);

    /// <summary>Gives the machine the rule it follows (PUT /api/update/rule); false when it didn't take it.</summary>
    Task<bool> PushRuleAsync(RemoteMachine machine, string rule, CancellationToken ct);
}

/// <summary>One machine's part of a rollout. <c>State</c> is <c>pending</c>, <c>checking</c>, <c>waiting</c>, <c>restarting</c>, <c>done</c>, <c>rolled-back</c>, <c>no-return</c> or <c>skipped</c>.</summary>
public sealed record RolloutStep(string MachineId, string Name, string State, string? Detail = null, string? FromVersion = null, string? ToVersion = null, int WaitingSessions = 0);

/// <summary>A rollout, running or finished. <c>Status</c> is <c>running</c>, <c>done</c>, <c>stopped</c> (a machine failed; the rest were left alone) or <c>cancelled</c>.</summary>
public sealed record RolloutRun(string Id, string Status, string StartedAt, string? FinishedAt, IReadOnlyList<RolloutStep> Steps, string? Summary);

/// <summary>
/// Restarts the other machines into the update they have staged, one at a time, from this Fleet so it carries on when
/// the tab closes. For each: skips what is unreachable or has nothing new, waits while sessions are working, asks it to
/// restart, and waits for it to come back on the new version. A machine that comes back on the old version (its update
/// failed and was undone) or doesn't come back stops the rollout. This machine is never part of it.
/// </summary>
public sealed partial class UpdateRolloutService(
    RemoteMachineService machines,
    IUpdateNodes nodes,
    IEventBroadcaster broadcaster,
    TimeProvider time,
    ILogger<UpdateRolloutService> logger) : IAsyncDisposable
{
    public const string EventType = "update.rollout";

    /// <summary>How often a busy machine is looked at again.</summary>
    public static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for a busy machine before leaving it for later.</summary>
    public static readonly TimeSpan BusyLimit = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan ComeBackPoll = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan ComeBackLimit = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _runCancel;
    private RolloutRun? _run;
    private Task _task = Task.CompletedTask;

    /// <summary>The running rollout, or the last one; null before the first.</summary>
    public RolloutRun? Current
    {
        get
        {
            lock (_gate)
                return _run;
        }
    }

    /// <summary>Finishes when the current rollout does (for tests).</summary>
    public Task Completion => _task;

    /// <summary>Starts a rollout over <paramref name="machineIds"/> (all machines when empty). Null when one is running already.</summary>
    public async Task<RolloutRun?> StartAsync(IReadOnlyCollection<string>? machineIds)
    {
        var list = (await machines.ListAsync()).Where(m => machineIds is not { Count: > 0 } || machineIds.Contains(m.Id)).ToList();
        lock (_gate)
        {
            if (_run is { Status: "running" })
                return null;

            _run = new RolloutRun(
                Guid.NewGuid().ToString("N"),
                "running",
                time.GetUtcNow().ToString("O"),
                null,
                [.. list.Select(m => new RolloutStep(m.Id, m.Name, "pending"))],
                null);
            _runCancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            var token = _runCancel.Token;
            _task = Task.Run(() => RunAsync(list, token));
            return _run;
        }
    }

    /// <summary>Stops after the machine in hand; the ones not reached stay as they are.</summary>
    public void Cancel()
    {
        lock (_gate)
            _runCancel?.Cancel();
    }

    private async Task RunAsync(IReadOnlyList<RemoteMachine> list, CancellationToken ct)
    {
        string status = "done";
        string? summary = null;
        try
        {
            foreach (var machine in list)
            {
                var result = await UpdateOneAsync(machine, ct);
                if (result is { } failure)
                {
                    status = "stopped";
                    summary = failure;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            summary = "Stopped before it finished.";
        }
        catch (Exception ex)
        {
            LogRolloutFailed(ex);
            status = "stopped";
            summary = "The rollout stopped because of an error.";
        }

        Update(run => run with { Status = status, FinishedAt = time.GetUtcNow().ToString("O"), Summary = summary });
        await PublishAsync();
    }

    /// <summary>Null when the machine is fine (updated, or left alone for a good reason); otherwise what went wrong, which stops the rollout.</summary>
    private async Task<string?> UpdateOneAsync(RemoteMachine machine, CancellationToken ct)
    {
        await SetAsync(machine, "checking");
        var info = await nodes.ReadAsync(machine, ct);
        if (info is null)
            return await SkipAsync(machine, "can't be reached");
        if (info.StagedVersion is null)
            return await SkipAsync(machine, "nothing downloaded yet", info.Version);
        if (!UpdateCheckService.IsNewerVersion(info.StagedVersion, info.Version))
            return await SkipAsync(machine, "already up to date", info.Version);
        if (!info.CanRestart)
            return await SkipAsync(machine, info.RestartUnavailableReason ?? "can't restart itself", info.Version);

        var target = info.StagedVersion;
        var from = info.Version;

        var waitingSince = time.GetTimestamp();
        while (true)
        {
            if (info!.Working > 0)
            {
                await SetAsync(machine, "waiting", $"waiting for {info.Working} {(info.Working == 1 ? "session" : "sessions")} to finish", from, target, info.Working);
                if (time.GetElapsedTime(waitingSince) > BusyLimit)
                    return await SkipAsync(machine, "still busy, left for later", from);

                await Task.Delay(BusyPoll, time, ct);
                info = await nodes.ReadAsync(machine, ct);
                if (info is null)
                    return await SkipAsync(machine, "can't be reached", from);
                continue;
            }

            var applied = await nodes.ApplyAsync(machine, ct);
            if (applied.Outcome == NodeApplyOutcome.Accepted)
                break;
            if (applied.Outcome == NodeApplyOutcome.Busy)
            {
                // A turn started between looking and asking: back to waiting.
                info = info with { Working = Math.Max(1, info.Working) };
                continue;
            }

            return await SkipAsync(machine, applied.Outcome == NodeApplyOutcome.Unreachable ? "can't be reached" : applied.Reason ?? "refused to restart", from);
        }

        await SetAsync(machine, "restarting", $"restarting into {target}", from, target);
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < ComeBackLimit)
        {
            await Task.Delay(ComeBackPoll, time, ct);
            var now = await nodes.ReadAsync(machine, ct);
            if (now is null)
                continue;
            if (string.Equals(now.Version, target, StringComparison.OrdinalIgnoreCase))
            {
                await SetAsync(machine, "done", $"back on {target}", from, target);
                return null;
            }

            if (now.RolledBack is { } rolledBack && string.Equals(rolledBack.To, target, StringComparison.OrdinalIgnoreCase))
            {
                var why = $"{machine.Name} didn't start on {target} ({rolledBack.Reason}) and went back to {now.Version}.";
                await SetAsync(machine, "rolled-back", $"went back to {now.Version}: {rolledBack.Reason}", from, target);
                return why;
            }
        }

        await SetAsync(machine, "no-return", "didn't come back within 3 minutes", from, target);
        return $"{machine.Name} didn't come back within 3 minutes.";
    }

    private async Task<string?> SkipAsync(RemoteMachine machine, string reason, string? from = null)
    {
        await SetAsync(machine, "skipped", reason, from);
        return null;
    }

    private async Task SetAsync(RemoteMachine machine, string state, string? detail = null, string? from = null, string? to = null, int waiting = 0)
    {
        Update(run => run with
        {
            Steps = [.. run.Steps.Select(s => s.MachineId == machine.Id ? s with { State = state, Detail = detail, FromVersion = from, ToVersion = to, WaitingSessions = waiting } : s)],
        });
        await PublishAsync();
    }

    private void Update(Func<RolloutRun, RolloutRun> change)
    {
        lock (_gate)
        {
            if (_run is not null)
                _run = change(_run);
        }
    }

    private async Task PublishAsync()
    {
        var run = Current;
        if (run is null)
            return;

        try
        {
            await broadcaster.BroadcastAsync("sessions", EventType, JsonSerializer.SerializeToElement(run, InfrastructureJsonContext.Default.RolloutRun), userId: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogPublishFailed(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _task;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The update rollout failed.")]
    private partial void LogRolloutFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't publish the update rollout.")]
    private partial void LogPublishFailed(Exception ex);
}
