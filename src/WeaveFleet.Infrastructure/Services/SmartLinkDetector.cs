using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>A GitHub link found in a session's messages, waiting to be stored.</summary>
public sealed record DetectedSmartLink(
    string SessionId,
    string UserId,
    GitHubLinkReference Reference,
    string Relationship);

/// <summary>
/// Finds GitHub pull requests and issues in message parts as they stream through the relay and queues
/// them for <see cref="SmartLinkWatcherService"/>. Text parts count as mentions. A pull request URL in the
/// output of a tool call that ran <c>gh pr create</c> is the session's own pull request.
/// </summary>
public sealed class SmartLinkDetector
{
    private const int MaxSeen = 20_000;
    private const int MaxCollectedChars = 64 * 1024;

    private readonly Channel<DetectedSmartLink> _channel = Channel.CreateBounded<DetectedSmartLink>(
        new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Text parts are re-sent as they stream, so remember what was already queued.
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.OrdinalIgnoreCase);

    public ChannelReader<DetectedSmartLink> Reader => _channel.Reader;

    public void Observe(string sessionId, string? userId, string eventType, JsonElement? payload)
    {
        if (eventType != EventTypes.MessagePartUpdated || string.IsNullOrEmpty(userId) || payload is not { } value)
            return;

        foreach (var (reference, relationship) in FindLinks(value))
        {
            if (_seen.Count >= MaxSeen)
                _seen.Clear();

            if (!_seen.TryAdd($"{sessionId}|{reference.ResourceId}|{relationship}", 0))
                continue;

            _channel.Writer.TryWrite(new DetectedSmartLink(sessionId, userId, reference, relationship));
        }
    }

    /// <summary>Extracts links and their relationship from a <c>message.part.updated</c> payload.</summary>
    internal static IReadOnlyList<(GitHubLinkReference Reference, string Relationship)> FindLinks(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("part", out var part)
            || part.ValueKind != JsonValueKind.Object
            || !part.TryGetProperty("type", out var typeElement))
        {
            return [];
        }

        switch (typeElement.GetString())
        {
            case "text":
                var text = part.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                return [.. GitHubLinkParser.Extract(text, IsStreaming(part)).Select(r => (r, SmartLinkRelationships.Mentioned))];

            case "tool":
                if (!part.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
                    return [];

                var input = state.TryGetProperty("input", out var inputElement) ? Collect(inputElement) : string.Empty;
                if (!IsPullRequestCreation(input))
                    return [];

                var output = new StringBuilder();
                if (state.TryGetProperty("output", out var outputElement))
                    output.Append(Collect(outputElement));
                if (state.TryGetProperty("metadata", out var metadataElement))
                    output.Append('\n').Append(Collect(metadataElement));

                return [.. GitHubLinkParser.Extract(output.ToString())
                    .Where(r => r.ResourceType == GitHubLinkReference.PullRequest)
                    .Select(r => (r, SmartLinkRelationships.Own))];

            default:
                return [];
        }
    }

    /// <summary>
    /// A text part with a start time but no end time is still streaming. Parts without timing
    /// (harnesses that send whole blocks) are treated as complete.
    /// </summary>
    private static bool IsStreaming(JsonElement part)
        => part.TryGetProperty("time", out var time)
           && time.ValueKind == JsonValueKind.Object
           && (!time.TryGetProperty("end", out var end) || end.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);

    internal static bool IsPullRequestCreation(string command)
        => command.Contains("gh pr create", StringComparison.Ordinal)
           || command.Contains("gh pr new", StringComparison.Ordinal);

    private static string Collect(JsonElement element)
    {
        var builder = new StringBuilder();
        Collect(element, builder, depth: 0);
        return builder.ToString();
    }

    private static void Collect(JsonElement element, StringBuilder builder, int depth)
    {
        if (depth > 6 || builder.Length >= MaxCollectedChars)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(element.GetString()).Append('\n');
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value, builder, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Collect(item, builder, depth + 1);
                break;
        }
    }
}
