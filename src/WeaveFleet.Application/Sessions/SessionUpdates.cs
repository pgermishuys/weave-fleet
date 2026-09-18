using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Sessions;

/// <summary>What a session asked to be told: that the turn handling its message in another session ended.</summary>
/// <param name="AskerId">The session that sent the message and asked to hear back.</param>
/// <param name="TargetId">The session it messaged.</param>
/// <param name="UserId">Whose sessions they are.</param>
/// <param name="MessageId">The id the target's harness was given for the message. Its replies name it as their parent.</param>
public sealed record SessionUpdateWatch(string AskerId, string TargetId, string UserId, string MessageId);

/// <summary>An update ready for the session that asked, built when the target's turn ended.</summary>
public sealed record SessionUpdate(string AskerId, string TargetId, string UserId, string Text, string Outcome);

/// <summary>Reads how a watched turn went and hands the update to the session that asked.</summary>
public interface ISessionUpdateSender
{
    /// <summary>The update for a turn that ended, or <see langword="null"/> when there's nothing to send.</summary>
    Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct);

    /// <summary>Prompts the session that asked. False when it wasn't delivered.</summary>
    Task<bool> SendAsync(SessionUpdate update, CancellationToken ct);
}

/// <summary>
/// Tells a session when another session it messaged with <c>notifyWhenDone</c> has done it: one prompt when the
/// turn that handled the message ends, and nothing about any other turn.
/// </summary>
/// <remarks>
/// <para>
/// Which turn counts: the one whose replies name the message as their parent. A turn the target was already running
/// when the message arrived doesn't; OpenCode runs the message after it, and only then do replies name it.
/// </para>
/// <para>
/// When the asker is itself mid-turn, the update waits for that turn to end. A prompt to a busy OpenCode session
/// isn't refused, it's queued into the running turn, so sending it at once would change what the asker is doing.
/// </para>
/// <para>
/// A turn that an update started can't ask to be told again (<see cref="IsStartedByUpdate"/>). Otherwise two
/// sessions that each ask could wake each other forever, a turn each time, with nobody at the keyboard.
/// </para>
/// <para>
/// All of it is in memory. A Fleet restart stops every session's turn, so a watched turn can't finish afterwards
/// and there'd be nothing to deliver.
/// </para>
/// </remarks>
public sealed partial class SessionUpdates(
    SessionActivityTracker activity,
    IServiceScopeFactory scopeFactory,
    ILogger<SessionUpdates> logger)
{
    public const string Finished = "finished";
    public const string Failed = "failed";

    // A message the target never gets to (it was aborted, or deleted) would otherwise be watched for good.
    private static readonly TimeSpan WatchLifetime = TimeSpan.FromDays(1);

    private readonly Lock _gate = new();
    private readonly List<Watched> _watches = [];
    private readonly Dictionary<string, Queue<SessionUpdate>> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedByUpdate = new(StringComparer.Ordinal);
    private readonly List<Task> _inFlight = [];

    /// <summary>Finished when every update in flight has been read and sent or held. Tests wait on it.</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
                return Task.WhenAll(_inFlight);
        }
    }

    /// <summary>Watches for the end of the turn that handles <paramref name="watch"/>'s message.</summary>
    public void Watch(SessionUpdateWatch watch)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow - WatchLifetime;
            _watches.RemoveAll(w => w.At < cutoff);
            _watches.Add(new Watched(watch, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Whether the turn this session is on was started by an update Fleet sent it. Such a turn may message other
    /// sessions, but not ask to be told again.
    /// </summary>
    public bool IsStartedByUpdate(string sessionId)
    {
        lock (_gate)
            return _startedByUpdate.Contains(sessionId);
    }

    /// <summary>Called for every event the relay translates, on its pump.</summary>
    public void Observe(string sessionId, DomainEvent? domainEvent)
    {
        switch (domainEvent)
        {
            case MessageCreated { Payload.Info: var info }:
                Arm(sessionId, info);
                break;
            case MessageUpdated { Payload.Info: var info }:
                Arm(sessionId, info);
                break;
            case TurnFailed failed:
                lock (_gate)
                {
                    foreach (var w in _watches.Where(w => w.Armed && w.Watch.TargetId == sessionId))
                        w.Failure = failed.Payload.Error;
                }

                break;
            case SessionIdled:
                OnIdle(sessionId);
                break;
        }
    }

    private void Arm(string sessionId, MessageEventInfo info)
    {
        if (info.Role != "assistant" || string.IsNullOrEmpty(info.ParentId))
            return;

        lock (_gate)
        {
            foreach (var w in _watches.Where(w => w.Watch.TargetId == sessionId && w.Watch.MessageId == info.ParentId))
                w.Armed = true;
        }
    }

    private void OnIdle(string sessionId)
    {
        List<Watched> ended;
        lock (_gate)
        {
            ended = _watches.Where(w => w.Armed && w.Watch.TargetId == sessionId).ToList();
            _watches.RemoveAll(ended.Contains);

            // The turn an update started is over; the next one is someone else's.
            _startedByUpdate.Remove(sessionId);
        }

        // Off the relay's pump: reading the reply and prompting both go to the harness.
        foreach (var w in ended)
            Track(Task.Run(() => ReportAsync(w)));

        // This session may be waiting to be told something itself.
        Track(Task.Run(() => SendHeldAsync(sessionId)));
    }

    private void Track(Task task)
    {
        lock (_gate)
            _inFlight.Add(task);
        _ = task.ContinueWith(
            done =>
            {
                lock (_gate)
                    _inFlight.Remove(done);
            },
            TaskScheduler.Default);
    }

    private async Task ReportAsync(Watched ended)
    {
        var watch = ended.Watch;
        try
        {
            SessionUpdate? update;
            using (var scope = BeginScope(watch.UserId))
                update = await scope.Sender.ReadAsync(watch, ended.Failure, CancellationToken.None).ConfigureAwait(false);
            if (update is null)
                return;

            lock (_gate)
            {
                if (!_held.TryGetValue(watch.AskerId, out var queue))
                    _held[watch.AskerId] = queue = new Queue<SessionUpdate>();
                queue.Enqueue(update);
            }

            // Mid-turn, it hears when that turn ends; OnIdle sends it then.
            if (!SessionActivityTracker.IsInTurn(activity.Get(watch.AskerId)?.ActivityStatus))
                await SendHeldAsync(watch.AskerId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReportFailed(ex, watch.TargetId, watch.AskerId);
        }
    }

    /// <summary>Sends the oldest update held for a session. One per turn: the rest wait for it to end.</summary>
    private async Task SendHeldAsync(string askerId)
    {
        SessionUpdate update;
        lock (_gate)
        {
            // An update it's already working on: the next waits for that turn to end.
            if (_startedByUpdate.Contains(askerId)
                || !_held.TryGetValue(askerId, out var queue)
                || !queue.TryDequeue(out update!))
            {
                return;
            }

            if (queue.Count == 0)
                _held.Remove(askerId);

            // Before the prompt, so a fleet_message the new turn makes straight away is already held to it.
            _startedByUpdate.Add(askerId);
        }

        try
        {
            using var scope = BeginScope(update.UserId);
            if (await scope.Sender.SendAsync(update, CancellationToken.None).ConfigureAwait(false))
                return;
        }
        catch (Exception ex)
        {
            LogSendFailed(ex, update.TargetId, askerId);
        }

        lock (_gate)
            _startedByUpdate.Remove(askerId);
    }

    /// <summary>A scope that acts as the sessions' owner, as the relay's pump has no user of its own.</summary>
    private SenderScope BeginScope(string userId)
    {
        var scope = scopeFactory.CreateScope();
        return new SenderScope(scope, scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(userId));
    }

    private sealed class Watched(SessionUpdateWatch watch, DateTimeOffset at)
    {
        public SessionUpdateWatch Watch { get; } = watch;
        public DateTimeOffset At { get; } = at;
        public bool Armed { get; set; }
        public TurnError? Failure { get; set; }
    }

    private sealed class SenderScope(IServiceScope scope, IDisposable user) : IDisposable
    {
        public ISessionUpdateSender Sender { get; } = scope.ServiceProvider.GetRequiredService<ISessionUpdateSender>();

        public void Dispose()
        {
            user.Dispose();
            scope.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read how session {TargetId} did for session {AskerId}")]
    private partial void LogReportFailed(Exception ex, string targetId, string askerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tell session {AskerId} that session {TargetId} is done")]
    private partial void LogSendFailed(Exception ex, string targetId, string askerId);
}

/// <summary>
/// Builds an update from what the target session already has (its last reply, or the failure) and prompts the
/// session that asked. No model call: it costs the asker one turn and nothing else.
/// </summary>
public sealed class SessionUpdateSender(
    SessionMessagesFeature feature,
    ISessionRepository sessions,
    ISessionMessageProxy messages,
    SessionOrchestrator orchestrator,
    IEventBroadcaster broadcaster) : ISessionUpdateSender
{
    /// <summary>How much of the target's reply the asker gets. Enough to act on; the rest is in that session.</summary>
    public const int MaxReplyLength = 600;

    public async Task<SessionUpdate?> ReadAsync(SessionUpdateWatch watch, TurnError? failure, CancellationToken ct)
    {
        var target = await sessions.GetByIdAsync(watch.TargetId).ConfigureAwait(false);
        if (target is null)
            return null;

        string body;
        if (failure is not null)
        {
            body = failure.Message;
        }
        else
        {
            var page = await messages.GetMessagesAsync(watch.TargetId, limit: 30, ct: ct).ConfigureAwait(false);
            body = LastReply(page.Messages, watch.MessageId) ?? "It finished without writing a reply.";
        }

        var outcome = failure is null ? SessionUpdates.Finished : SessionUpdates.Failed;
        return new SessionUpdate(
            watch.AskerId,
            watch.TargetId,
            watch.UserId,
            SessionMessages.WrapUpdate(watch.TargetId, target.Title, outcome, Shorten(body)),
            outcome);
    }

    public async Task<bool> SendAsync(SessionUpdate update, CancellationToken ct)
    {
        // Checked when it's sent, not when it was asked for: turning the switch off stops the ones still waiting.
        if (!await feature.IsEnabledAsync().ConfigureAwait(false))
            return false;

        var sent = await orchestrator.PromptSessionWithReceiptAsync(
                update.AskerId,
                update.Text,
                options: null,
                userMessageId: null,
                correlationId: null,
                ct)
            .ConfigureAwait(false);
        if (sent.IsFailure)
            return false;

        var payload = new SessionReportedPayload
        {
            FromSessionId = update.TargetId,
            ToSessionId = update.AskerId,
            Outcome = update.Outcome,
            CorrelationId = sent.Value.CorrelationId,
        };
        await broadcaster.BroadcastAsync(
                $"session:{update.AskerId}",
                "session.reported",
                JsonSerializer.SerializeToElement(payload, ApplicationJsonContext.Default.SessionReportedPayload),
                new SessionReported { Payload = payload },
                update.UserId,
                ct)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The text of the last reply after the message, or of the last reply at all when the message isn't in the page
    /// (a harness that keeps its own ids).
    /// </summary>
    internal static string? LastReply(IReadOnlyList<HarnessMessage> page, string messageId)
    {
        var start = 0;
        for (var i = 0; i < page.Count; i++)
        {
            if (page[i].Id == messageId)
                start = i + 1;
        }

        for (var i = page.Count - 1; i >= start; i--)
        {
            if (page[i].Role == "assistant" && !string.IsNullOrWhiteSpace(page[i].TextContent))
                return page[i].TextContent.Trim();
        }

        return null;
    }

    /// <summary>Cuts a long reply at a word, with an ellipsis, so the asker's prompt stays short.</summary>
    internal static string Shorten(string text)
    {
        text = text.Trim();
        if (text.Length <= MaxReplyLength)
            return text;

        var cut = text.LastIndexOf(' ', MaxReplyLength);
        return text[..(cut > MaxReplyLength / 2 ? cut : MaxReplyLength)].TrimEnd() + "…";
    }
}
