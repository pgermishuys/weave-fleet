using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Mods;

/// <summary>
/// Tells the user's mods a turn ended (<c>turn.complete</c>, <c>docs/mods/api.md</c>, "Events"). The relay hands it the
/// session's events as they pass; <see cref="PromptQueueDispatcher"/> asks it to dispatch when the session idles, before
/// the queue's next message goes. It never blocks or breaks the queue: whatever goes wrong, the queue goes on.
/// </summary>
public sealed partial class ModTurnHook(IModHost host, ModFollowUps followUps, TimeProvider time, ILogger<ModTurnHook> logger)
{
    /// <summary>How long Fleet waits for the mods' hooks before the queue goes on.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Turn> _turns = new(StringComparer.Ordinal);

    private sealed class Turn
    {
        public List<string> Files { get; } = [];
        public string TurnId { get; set; } = string.Empty;
        public string? Failure { get; set; }
        public bool Aborted { get; set; }
    }

    /// <summary>
    /// Called for every event the relay translates. What a turn changed is noted from its file-writing tool calls, from
    /// the end of the last <c>turn.complete</c> on: a turn is several assistant messages, so <see cref="TurnStarted"/>
    /// is no place to start again.
    /// </summary>
    public void Observe(string sessionId, DomainEvent? domainEvent)
    {
        switch (domainEvent)
        {
            case FilesWritten written:
                With(sessionId, t => t.Files.AddRange(written.Payload.Paths));
                break;
            case TurnEnded ended:
                With(sessionId, t => t.TurnId = ended.Payload.MessageId);
                break;
            case TurnFailed failed:
                With(sessionId, t => t.Failure = failed.Payload.Error.Message);
                break;
        }
    }

    /// <summary>The user stopped the session's turn with Stop.</summary>
    public void Aborted(string sessionId) => With(sessionId, t => t.Aborted = true);

    private void With(string sessionId, Action<Turn> note)
    {
        lock (_gate)
        {
            if (!_turns.TryGetValue(sessionId, out var turn))
                _turns[sessionId] = turn = new Turn();
            note(turn);
        }
    }

    /// <summary>
    /// Dispatches <c>turn.complete</c> for the session whose turn just ended and waits up to <see cref="Wait"/>. A
    /// follow-up a hook queues in that time is added to the queue and not sent here; the caller sends it next. Side
    /// conversations and an agent's delegated child sessions are not the user's turns, and are skipped.
    /// </summary>
    public async Task CompleteAsync(string userId, string sessionId, string? directory, bool notTheUsersTurn, CancellationToken ct = default)
    {
        Turn? turn;
        lock (_gate)
        {
            _turns.Remove(sessionId, out turn);
        }

        if (notTheUsersTurn)
            return;

        try
        {
            turn ??= new Turn();
            var e = Event(sessionId, turn, directory);
            using var window = followUps.BeginTurnDispatch(sessionId);
            var dispatch = host.DispatchAsync(userId, new ModDispatchRequest("turn.complete", sessionId, e), ct);
            try
            {
                await dispatch.WaitAsync(Wait, time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The host is left alone; the dispatch finishes on its own and its outcome is dropped.
                _ = dispatch.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                LogTimedOut(Wait.TotalSeconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(ex);
        }
    }

    private static JsonElement Event(string sessionId, Turn turn, string? directory)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("turnId", turn.TurnId);
            writer.WriteBoolean("isAborted", turn.Aborted);
            writer.WriteBoolean("isFailed", turn.Failure is not null);
            if (turn.Failure is not null)
                writer.WriteString("failure", turn.Failure);
            writer.WriteStartArray("changedFiles");
            foreach (var path in turn.Files.Select(p => Relative(p, directory)).Distinct(StringComparer.Ordinal))
                writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    /// <summary>The path relative to the session's folder when it is inside it, else as given.</summary>
    internal static string Relative(string path, string? directory)
    {
        if (string.IsNullOrEmpty(directory) || !Path.IsPathRooted(path))
            return path;
        var relative = Path.GetRelativePath(directory, path);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : relative.Replace('\\', '/');
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A turn.complete hook took longer than {Seconds} s; the queue goes on.")]
    private partial void LogTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "turn.complete could not be dispatched; the queue goes on.")]
    private partial void LogFailed(Exception ex);
}
