using System.Text;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// The text of the replies streaming now, as their deltas built it, until each turn ends. A harness may keep a part's
/// text only once the part ends (OpenCode) or a message only once it's done (Claude Code), so a snapshot taken mid-reply
/// would have none of the reply, and opening the session (or coming back to it, or reloading) would show only what
/// streams after that.
/// <see cref="Overlay"/> puts the text so far into the snapshot.
/// </summary>
/// <remarks>
/// Each text delta goes out with its <see cref="MessagePartDeltaStreamedPayload.Offset"/>, where it starts in the part's
/// text. A client that joins mid-reply gets the text so far in the snapshot and the deltas from about then on, some of
/// them already in that text; the offset tells it which.
/// </remarks>
public sealed class StreamingReplies(TimeProvider timeProvider)
{
    private readonly Dictionary<string, Dictionary<string, StreamingMessage>> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>
    /// Called for every event the relay translates, on its pump, before it goes out. Returns the event to send: a text
    /// delta with its offset, any other event as it came.
    /// </summary>
    public DomainEvent? Observe(string sessionId, DomainEvent? domainEvent)
    {
        switch (domainEvent)
        {
            case MessagePartDeltaStreamed { Payload.Field: "text" } delta:
                return delta with { Payload = delta.Payload with { Offset = Append(sessionId, delta.Payload) } };

            // By the end of a turn the harness has kept its text.
            case TurnEnded or SessionIdled:
                Forget(sessionId);
                break;
        }

        return domainEvent;
    }

    /// <summary>Forgets the session's replies: its harness went away, and the deltas with it.</summary>
    public void Forget(string sessionId)
    {
        lock (_sync)
            _sessions.Remove(sessionId);
    }

    /// <summary>
    /// The newest page of the session's messages with the text streamed so far: a part with less text than has
    /// streamed gets it, and a reply the page doesn't have yet is added at the end.
    /// </summary>
    public IReadOnlyList<MessageLifecyclePayload> Overlay(string sessionId, IReadOnlyList<MessageLifecyclePayload> messages)
    {
        List<(string MessageId, long Created, List<(string PartId, string Text)> Parts)> streaming;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var replies))
                return messages;

            streaming = replies.Select(reply => (
                reply.Key,
                reply.Value.Created,
                reply.Value.Parts.Select(part => (part.Key, part.Value.ToString())).ToList())).ToList();
        }

        var overlaid = messages.ToList();
        foreach (var (messageId, created, parts) in streaming)
        {
            var index = overlaid.FindIndex(message => message.Info.Id == messageId);
            if (index == -1)
            {
                overlaid.Add(new MessageLifecyclePayload
                {
                    Info = new MessageEventInfo
                    {
                        Id = messageId,
                        Role = "assistant",
                        SessionId = sessionId,
                        Time = new MessageEventTime { Created = created },
                    },
                    Parts = parts.Select(part => (MessageEventPart)TextPart(sessionId, messageId, part.PartId, part.Text)).ToList(),
                });
                continue;
            }

            overlaid[index] = overlaid[index] with { Parts = WithText(overlaid[index].Parts, sessionId, messageId, parts) };
        }

        return overlaid;
    }

    private int Append(string sessionId, MessagePartDeltaStreamedPayload delta)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(sessionId, out var replies))
                _sessions[sessionId] = replies = new Dictionary<string, StreamingMessage>(StringComparer.Ordinal);

            if (!replies.TryGetValue(delta.MessageId, out var reply))
                replies[delta.MessageId] = reply = new StreamingMessage(timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

            if (!reply.Parts.TryGetValue(delta.PartId, out var text))
                reply.Parts[delta.PartId] = text = new StringBuilder();

            var offset = text.Length;
            text.Append(delta.Delta);
            return offset;
        }
    }

    /// <summary>The message's parts, each streamed part with its text so far unless the harness already has more.</summary>
    private static List<MessageEventPart> WithText(
        IReadOnlyList<MessageEventPart> parts,
        string sessionId,
        string messageId,
        List<(string PartId, string Text)> streamed)
    {
        var updated = parts.ToList();
        foreach (var (partId, text) in streamed)
        {
            var index = updated.FindIndex(part => part.Id == partId);
            switch (index == -1 ? null : updated[index])
            {
                case null:
                    updated.Add(TextPart(sessionId, messageId, partId, text));
                    break;
                case TextMessageEventPart part when part.Text.Length < text.Length:
                    updated[index] = part with { Text = text };
                    break;
                // OpenCode streams a reasoning part's text as "text" deltas too.
                case ReasoningMessageEventPart part when part.Text.Length < text.Length:
                    updated[index] = part with { Text = text };
                    break;
            }
        }

        return updated;
    }

    private static TextMessageEventPart TextPart(string sessionId, string messageId, string partId, string text)
        => new() { Id = partId, SessionId = sessionId, MessageId = messageId, Text = text };

    private sealed record StreamingMessage(long Created)
    {
        public Dictionary<string, StringBuilder> Parts { get; } = new(StringComparer.Ordinal);
    }
}
