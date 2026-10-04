using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// Takes the running-work events every harness sends (<see cref="EventTypes.WorkStarted"/>,
/// <see cref="EventTypes.WorkUpdated"/>, <see cref="EventTypes.WorkEnded"/>) from the relay to
/// <see cref="DelegationService"/>, which records them. Work with a child session gets a Fleet session of its own
/// first (<see cref="SessionOrchestrator.EnsureDelegatedChildSessionAsync"/>), hidden under its parent.
/// </summary>
/// <remarks>
/// Each session's events are handled one at a time in the order its harness sent them, off the relay's pump: making a
/// child session takes a moment, the conversation's events shouldn't wait for it, and a "running" report handled after
/// "ended" would leave the work running for good.
/// </remarks>
public sealed partial class RunningWorkRecorder(IServiceScopeFactory scopeFactory, ILogger<RunningWorkRecorder> logger)
{
    private readonly ConcurrentDictionary<string, Task> _tails = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>Queues a work event of session <paramref name="fleetSessionId"/>'s harness.</summary>
    public void Observe(string fleetSessionId, string? userId, HarnessEvent evt)
    {
        if (WorkEvents.Read(evt) is not { } report)
        {
            LogUnreadable(logger, evt.Type, fleetSessionId);
            return;
        }

        Queue(fleetSessionId, () => RecordAsync(fleetSessionId, userId, evt.Type, report));
    }

    /// <summary>
    /// Catches up with what <paramref name="instance"/> says is running now (<see cref="IHarnessSession.GetRunningWorkAsync"/>),
    /// when its harness can say: Fleet's running work that isn't there ended with the harness. Queued behind the
    /// session's events, so work reported before is recorded first. Work only the harness has isn't added: its own
    /// events say what it is.
    /// </summary>
    public void Reconcile(string fleetSessionId, string? userId, IHarnessSession instance, CancellationToken ct)
        => Queue(fleetSessionId, async () =>
        {
            var running = await instance.GetRunningWorkAsync(ct).ConfigureAwait(false);
            if (running is null)
                return;

            using var user = userId is null ? null : BackgroundUserContext.BeginScope(userId);
            using var scope = scopeFactory.CreateScope();
            var work = scope.ServiceProvider.GetRequiredService<DelegationService>();
            var lost = await work.SettleLostWorkAsync(fleetSessionId, running).ConfigureAwait(false);
            if (lost > 0)
                LogSettledLost(logger, lost, fleetSessionId);
        });

    /// <summary>Completes when every event queued for the session so far is handled.</summary>
    internal Task Idle(string fleetSessionId) => _tails.TryGetValue(fleetSessionId, out var tail) ? tail : Task.CompletedTask;

    private void Queue(string fleetSessionId, Func<Task> handle)
    {
        lock (_sync)
        {
            var tail = _tails.GetValueOrDefault(fleetSessionId) ?? Task.CompletedTask;
            _tails[fleetSessionId] = tail
                .ContinueWith(_ => RunAsync(fleetSessionId, handle), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task RunAsync(string fleetSessionId, Func<Task> handle)
    {
        try
        {
            await handle().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailed(logger, fleetSessionId, ex);
        }
    }

    private async Task RecordAsync(string fleetSessionId, string? userId, string type, WorkReport report)
    {
        using var user = userId is null ? null : BackgroundUserContext.BeginScope(userId);
        using var scope = scopeFactory.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<DelegationService>();

        // An end that says what the work was may be the first Fleet hears of it (it attached after the work started).
        if (type != EventTypes.WorkEnded || report.Kind is not null)
        {
            var childSessionId = await EnsureChildSessionAsync(scope.ServiceProvider, fleetSessionId, report).ConfigureAwait(false);
            await work.HandleWorkReportedAsync(fleetSessionId, report, childSessionId).ConfigureAwait(false);
        }

        if (type == EventTypes.WorkEnded)
            await work.HandleWorkEndedAsync(fleetSessionId, report.WorkId, report.EndedReason, report.Detail).ConfigureAwait(false);
    }

    /// <summary>The Fleet session for the work's child session, made when it's new; null when there's none or it failed.</summary>
    private async Task<string?> EnsureChildSessionAsync(IServiceProvider services, string fleetSessionId, WorkReport report)
    {
        if (report.ChildHarnessSessionId is not { Length: > 0 } childHarnessSessionId)
            return null;

        if (services.GetService<SessionOrchestrator>() is not { } orchestrator)
            return null;

        // The work is still recorded when this fails; only the link to its child's activity is missing.
        var title = report.Label ?? report.Title ?? WorkKinds.Subagent;
        try
        {
            var child = await orchestrator.EnsureDelegatedChildSessionAsync(fleetSessionId, childHarnessSessionId, title).ConfigureAwait(false);
            if (child.IsSuccess)
                return child.Value.Id;

            LogChildFailed(logger, childHarnessSessionId, fleetSessionId, child.Error.Description);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogChildFailed(logger, childHarnessSessionId, fleetSessionId, ex.Message);
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record running work for session {SessionId}")]
    private static partial void LogFailed(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped a {EventType} event of session {SessionId} that names no work")]
    private static partial void LogUnreadable(ILogger logger, string eventType, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} piece(s) of running work of session {SessionId} ended with its harness")]
    private static partial void LogSettledLost(ILogger logger, int count, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't make a Fleet session for child session {ChildSessionId} of session {SessionId}: {Reason}")]
    private static partial void LogChildFailed(ILogger logger, string childSessionId, string sessionId, string reason);
}
