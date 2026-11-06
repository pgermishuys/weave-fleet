using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Git;
using WeaveFleet.Application.Sessions.Prompting;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>A session a restart cut off as clients show it.</summary>
public sealed record SessionInterruptionView(string At, bool Waiting, string? LastCall, int? ChangedFiles)
{
    public static SessionInterruptionView From(SessionInterruption interruption) => new(
        interruption.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        interruption.Waiting,
        interruption.LastCall,
        interruption.ChangedFiles);
}

/// <summary>A session whose last turn failed, as clients show it.</summary>
public sealed record SessionFailureView(string At, string Message)
{
    public static SessionFailureView From(SessionFailure failure) => new(
        failure.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        failure.Message);
}

/// <summary>The <c>session.failure</c> event: the session's failed last turn now, or <see langword="null"/> once a new turn started.</summary>
public sealed record SessionFailureChanged(
    string SessionId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] SessionFailureView? Failure);

/// <summary>The <c>session.interrupted</c> event: the session's interruption now, or <see langword="null"/> when it has none.</summary>
public sealed record SessionInterruptionChanged(
    string SessionId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] SessionInterruptionView? Interruption);

/// <summary>
/// Sessions a Fleet restart cut off. While a turn runs its session carries <c>turn_started_at</c> (<see cref="TurnFlagger"/>);
/// when Fleet starts again, <see cref="RecoverAsync"/> checks each flagged session against its history and keeps those
/// still cut off as <see cref="SessionInterruption"/>s. The session's row and conversation show one with Continue, which
/// sends the same "Continue where you left off." as a retry after a limit, as the user's own click.
/// </summary>
public sealed partial class SessionInterruptions(
    FleetOptions options,
    ISessionInterruptionRepository interruptions,
    ISessionRepository sessions,
    IWorkspaceRepository workspaces,
    ISessionMessageProxy messages,
    SessionPrompting prompting,
    IEventBroadcaster broadcaster,
    IUserContext userContext,
    IBackgroundUserScope backgroundUser,
    TimeProvider time,
    ILogger<SessionInterruptions> logger)
{
    /// <summary>The event carrying a session's interruption after it changed (<see cref="SessionInterruptionChanged"/>).</summary>
    public const string ChangedEvent = "session.interrupted";

    /// <summary>The event carrying a session's failed last turn after it changed (<see cref="SessionFailureChanged"/>).</summary>
    public const string FailureEvent = "session.failure";

    /// <summary>How many characters of a failure's message the list keeps.</summary>
    private const int FailureMessageLimit = 300;

    /// <summary>How many of a session's newest messages the history check reads.</summary>
    private const int HistoryWindow = 12;

    /// <summary>How many characters of a command the call's description keeps.</summary>
    private const int CommandLimit = 60;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    public bool Enabled => options.Harness.RestartContinue;

    public async Task<Result<SessionInterruption?>> GetAsync(string sessionId)
    {
        if (await sessions.GetByIdAsync(sessionId).ConfigureAwait(false) is null)
            return FleetError.NotFoundFor(nameof(Session), sessionId);
        return Enabled ? await interruptions.GetAsync(sessionId).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Startup, before sessions are marked stopped: turns each flagged session into an interruption, or clears it when
    /// its history shows the turn had finished. The flag is cleared either way, so the next start doesn't see it again.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return;

        var flagged = await interruptions.ListFlaggedAsync().ConfigureAwait(false);
        int confirmed = 0, cleared = 0;
        foreach (var turn in flagged)
        {
            try
            {
                using var user = backgroundUser.Begin(turn.UserId);
                var found = await RecoverOneAsync(turn, ct).ConfigureAwait(false);
                if (found)
                    confirmed++;
                else
                    cleared++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRecoverFailed(ex, turn.SessionId);
            }
        }

        if (flagged.Count > 0)
            LogRecovered(flagged.Count, confirmed, cleared);
    }

    private async Task<bool> RecoverOneAsync(FlaggedTurn turn, CancellationToken ct)
    {
        await interruptions.ClearTurnAsync(turn.SessionId).ConfigureAwait(false);
        var (finished, lastCall) = turn.Waiting ? (false, null) : await ReadHistoryAsync(turn.SessionId, ct).ConfigureAwait(false);
        if (finished)
            return false;

        await interruptions.SaveAsync(new SessionInterruption
        {
            SessionId = turn.SessionId,
            UserId = turn.UserId,
            At = turn.StartedAt,
            Waiting = turn.Waiting,
            LastCall = lastCall,
            ChangedFiles = await CountChangedFilesAsync(turn.SessionId).ConfigureAwait(false),
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Whether the newest assistant message ended its turn, and the tool call it left unfinished. History that can't be
    /// read counts as not finished: the user gets the card, which is one click, rather than silence.
    /// </summary>
    internal async Task<(bool Finished, string? LastCall)> ReadHistoryAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var page = await messages.GetMessagesAsync(sessionId, HistoryWindow, null, ct).ConfigureAwait(false);
            return Judge(page.Messages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHistoryUnreadable(ex, sessionId);
            return (false, null);
        }
    }

    internal static (bool Finished, string? LastCall) Judge(IReadOnlyList<HarnessMessage> history)
    {
        var last = history.OrderBy(message => message.Timestamp).LastOrDefault();
        if (last is not { Role: "assistant" })
            return (false, null);

        // History read outside a turn shows a call that never finished as failed with CutOffCall.Message.
        var open = last.Parts.OfType<ToolUsePart>().LastOrDefault(tool =>
            tool.State is ToolUseState.Pending or ToolUseState.Running
            || (tool.State is ToolUseState.Error && tool.Error == CutOffCall.Message));
        if (open is not null)
            return (false, DescribeCall(open));

        // A message that ended on its tool calls was waiting for the model to read their results: when the harness stops
        // (OpenCode 2 ends a running call with "Killed by SIGTERM"), the calls complete but nothing answers them.
        if (EndedOnToolCalls(last.Finish) && last.Parts.OfType<ToolUsePart>().LastOrDefault() is { } lastTool)
            return (false, DescribeCall(lastTool));
        return (!string.IsNullOrEmpty(last.Finish) || last.Error is not null, null);
    }

    private static bool EndedOnToolCalls(string? finish) => finish is "tool-calls" or "tool_calls" or "tool_use";

    /// <summary>The call's name, with the command it ran when it has one: "bash (bun test)".</summary>
    private static string DescribeCall(ToolUsePart tool)
    {
        if (tool.Arguments.ValueKind == JsonValueKind.Object
            && tool.Arguments.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
            && command.GetString()?.Trim() is { Length: > 0 } text)
        {
            var line = text.Split('\n', 2)[0].Trim();
            return $"{tool.ToolName} ({(line.Length > CommandLimit ? line[..CommandLimit] + "…" : line)})";
        }

        return tool.ToolName;
    }

    private async Task<int?> CountChangedFilesAsync(string sessionId)
    {
        try
        {
            var session = await sessions.GetByIdAsync(sessionId).ConfigureAwait(false);
            var workspace = session is null ? null : await workspaces.GetByIdAsync(session.WorkspaceId).ConfigureAwait(false);
            if (workspace is null || !Directory.Exists(workspace.Directory))
                return null;
            var status = await GitCommand.RunAsync(workspace.Directory, GitTimeout, "status", "--porcelain", "-z").ConfigureAwait(false);
            return status.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogGitUnreadable(ex, sessionId);
            return null;
        }
    }

    /// <summary>
    /// What Continue sends: names the call that didn't finish and the files changed, when Fleet stored them. With neither
    /// (or a lost question), the plain "Continue where you left off.".
    /// </summary>
    internal static string ContinueMessage(SessionInterruption interruption)
    {
        var call = interruption.Waiting ? null : interruption.LastCall;
        var files = interruption.Waiting ? null : interruption.ChangedFiles;
        if (string.IsNullOrWhiteSpace(call) && files is not > 0)
            return TurnRetryService.ContinueText;

        var text = "Fleet restarted and cut off your last turn.";
        if (!string.IsNullOrWhiteSpace(call))
            text += $" The call that didn't finish: {call}.";
        if (files is > 0)
            text += $" {files} {(files == 1 ? "file" : "files")} changed so far, still on disk.";
        return text + " Check where you got to and continue.";
    }

    /// <summary>Continue: sends <see cref="ContinueMessage"/> as the user's own message. Only one caller gets to send it.</summary>
    public async Task<Result<Unit>> ContinueAsync(string sessionId, CancellationToken ct = default)
    {
        var interruption = Enabled ? await interruptions.RemoveAsync(sessionId).ConfigureAwait(false) : null;
        if (interruption is null)
            return FleetError.NotFoundFor(nameof(SessionInterruption), sessionId);

        await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
        var sent = await prompting.PromptSessionAsync(
            sessionId,
            ContinueMessage(interruption),
            new PromptOptions { Delivery = PromptDelivery.Queue },
            ct).ConfigureAwait(false);
        if (!sent.IsSuccess)
            LogContinueFailed(sessionId, sent.Error.Description);
        return sent.IsSuccess ? Unit.Value : sent.Error;
    }

    /// <summary>Dismiss: the user takes it from here.</summary>
    public async Task<Result<Unit>> DismissAsync(string sessionId, CancellationToken ct = default)
    {
        if (!Enabled || await interruptions.RemoveAsync(sessionId).ConfigureAwait(false) is null)
            return FleetError.NotFoundFor(nameof(SessionInterruption), sessionId);
        await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
        return Unit.Value;
    }

    /// <summary>A turn started: one the user started before choosing replaces the card.</summary>
    internal async Task OnTurnStartedAsync(string sessionId, CancellationToken ct = default)
    {
        if (await interruptions.RemoveAsync(sessionId).ConfigureAwait(false) is not null)
            await BroadcastAsync(sessionId, null, ct).ConfigureAwait(false);
        await interruptions.FlagTurnAsync(sessionId, time.GetUtcNow(), waiting: false).ConfigureAwait(false);
    }

    internal Task OnTurnEndedAsync(string sessionId) => interruptions.ClearTurnAsync(sessionId);

    /// <summary>A turn stopped on a failure: the session's row says so until its next turn starts.</summary>
    internal async Task OnTurnFailedAsync(string sessionId, string message, CancellationToken ct = default)
    {
        // Harness errors can carry a stack trace; the row's tooltip wants the reason.
        var reason = message.Split('\n', 2)[0].Trim();
        var failure = new SessionFailure(time.GetUtcNow(), reason.Length > FailureMessageLimit ? reason[..FailureMessageLimit] : reason);
        await interruptions.SetFailureAsync(sessionId, failure.At, failure.Message).ConfigureAwait(false);
        await BroadcastFailureAsync(sessionId, failure, ct).ConfigureAwait(false);
    }

    /// <summary>A new turn started: the failure before it is no longer the session's last word.</summary>
    internal async Task OnTurnStartedClearFailureAsync(string sessionId, CancellationToken ct = default)
    {
        if (await interruptions.ClearFailureAsync(sessionId).ConfigureAwait(false))
            await BroadcastFailureAsync(sessionId, null, ct).ConfigureAwait(false);
    }

    private async Task BroadcastFailureAsync(string sessionId, SessionFailure? failure, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new SessionFailureChanged(sessionId, failure is null ? null : SessionFailureView.From(failure)),
            ApplicationJsonContext.Default.SessionFailureChanged);
        await broadcaster.BroadcastAsync($"session:{sessionId}", FailureEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
        await broadcaster.BroadcastAsync("sessions", FailureEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
    }

    internal Task OnWaitingChangedAsync(string sessionId, bool waiting) => interruptions.SetWaitingAsync(sessionId, waiting);

    private async Task BroadcastAsync(string sessionId, SessionInterruption? interruption, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToElement(
            new SessionInterruptionChanged(sessionId, interruption is null ? null : SessionInterruptionView.From(interruption)),
            ApplicationJsonContext.Default.SessionInterruptionChanged);
        await broadcaster.BroadcastAsync($"session:{sessionId}", ChangedEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
        await broadcaster.BroadcastAsync("sessions", ChangedEvent, payload, userContext.UserId, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restart check: {Flagged} sessions were mid-turn, {Confirmed} confirmed cut off, {Cleared} cleared as finished")]
    private partial void LogRecovered(int flagged, int confirmed, int cleared);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check whether session {SessionId} was cut off by the restart")]
    private partial void LogRecoverFailed(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read session {SessionId}'s history to check whether its turn finished")]
    private partial void LogHistoryUnreadable(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not count session {SessionId}'s changed files")]
    private partial void LogGitUnreadable(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not continue session {SessionId} after a restart: {Reason}")]
    private partial void LogContinueFailed(string sessionId, string reason);
}

/// <summary>
/// Keeps each session's <c>turn_started_at</c> up to date. The relay hands it every event it translates, on its pump;
/// the writes run one session at a time, in the order the events came, as the session's owner.
/// </summary>
public sealed partial class TurnFlagger(IServiceScopeFactory scopeFactory, FleetOptions options, ILogger<TurnFlagger> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);

    /// <summary>Finished when everything handed to the flagger so far has run. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_tails.Values);
        }
    }

    /// <param name="status">The harness's reported status, when this event was a status change.</param>
    public void Observe(string sessionId, string? userId, Domain.Events.DomainEvent? domainEvent, string? status = null)
    {
        if (string.IsNullOrEmpty(userId))
            return;

        // The restart flag only matters when Continue is on; the failed-turn mark the list shows is always kept.
        var restart = options.Harness.RestartContinue;
        Func<SessionInterruptions, CancellationToken, Task>? work = domainEvent switch
        {
            Domain.Events.TurnStarted => async (service, ct) =>
            {
                await service.OnTurnStartedClearFailureAsync(sessionId, ct).ConfigureAwait(false);
                if (restart)
                    await service.OnTurnStartedAsync(sessionId, ct).ConfigureAwait(false);
            },
            Domain.Events.TurnFailed failed => async (service, ct) =>
            {
                if (restart)
                    await service.OnTurnEndedAsync(sessionId).ConfigureAwait(false);
                await service.OnTurnFailedAsync(sessionId, failed.Payload.Error.Message, ct).ConfigureAwait(false);
            },
            _ when !restart => null,
            Domain.Events.SessionIdled => (service, _) => service.OnTurnEndedAsync(sessionId),
            _ when status == ActivityStatuses.WaitingInput => (service, _) => service.OnWaitingChangedAsync(sessionId, true),
            _ when status is not null => (service, _) => service.OnWaitingChangedAsync(sessionId, false),
            _ => null,
        };
        if (work is null)
            return;

        lock (_gate)
        {
            var previous = _tails.GetValueOrDefault(sessionId) ?? Task.CompletedTask;
            var next = previous.ContinueWith(_ => RunAsync(sessionId, userId!, work), TaskScheduler.Default).Unwrap();
            _tails[sessionId] = next;
            _ = next.ContinueWith(
                done =>
                {
                    lock (_gate)
                    {
                        if (_tails.TryGetValue(sessionId, out var tail) && tail == done)
                            _tails.Remove(sessionId);
                    }
                },
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(string sessionId, string userId, Func<SessionInterruptions, CancellationToken, Task> work)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId);
            await work(scope.ServiceProvider.GetRequiredService<SessionInterruptions>(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFlagFailed(ex, sessionId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the turn of session {SessionId}")]
    private partial void LogFlagFailed(Exception ex, string sessionId);
}
