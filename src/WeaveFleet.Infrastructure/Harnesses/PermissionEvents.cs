using System.Collections.Concurrent;
using System.Text.Json;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses;

/// <summary>Builds Fleet's own permission events, which every adapter sends in place of its harness's.</summary>
internal static class PermissionEvents
{
    /// <summary>A <see cref="EventTypes.PermissionAsked"/> event for <paramref name="ask"/>.</summary>
    internal static HarnessEvent Asked(PermissionAsk ask, string harnessSessionId, string? fleetSessionId = null) => new()
    {
        Type = EventTypes.PermissionAsked,
        SessionId = harnessSessionId,
        FleetSessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(ask, InfrastructureJsonContext.Default.PermissionAsk),
    };

    /// <summary>A <see cref="EventTypes.PermissionReplied"/> event: <paramref name="ask"/> no longer waits.</summary>
    internal static HarnessEvent Replied(PermissionAsk ask, string reply, string harnessSessionId, string? fleetSessionId = null) => new()
    {
        Type = EventTypes.PermissionReplied,
        SessionId = harnessSessionId,
        FleetSessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(
            new PermissionReplied { Id = ask.Id, SessionId = ask.SessionId, Reply = reply },
            InfrastructureJsonContext.Default.PermissionReplied),
    };

    /// <summary>
    /// The session's activity while asks wait: <c>waiting_input</c> (Needs you) while one does, <c>busy</c> once
    /// the last is answered and the turn goes on.
    /// </summary>
    internal static HarnessEvent Status(string status, string harnessSessionId, string? fleetSessionId = null) => new()
    {
        Type = EventTypes.SessionStatus,
        SessionId = harnessSessionId,
        FleetSessionId = fleetSessionId,
        Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonSerializer.SerializeToElement(
            new SessionStatusEventPayload { Status = new SessionStatusEventKind { Type = status } },
            InfrastructureJsonContext.Default.SessionStatusEventPayload),
    };

    /// <summary>The ask in a <see cref="EventTypes.PermissionAsked"/> event's payload, or <see langword="null"/>.</summary>
    internal static PermissionAsk? ReadAsk(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        try
        {
            return payload.Deserialize(InfrastructureJsonContext.Default.PermissionAsk);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The answer in a <see cref="EventTypes.PermissionReplied"/> event's payload, or <see langword="null"/>.</summary>
    internal static PermissionReplied? ReadReplied(HarnessEvent evt)
    {
        if (evt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return null;
        try
        {
            return payload.Deserialize(InfrastructureJsonContext.Default.PermissionReplied);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The text of <paramref name="value"/> when it's a non-empty JSON string.</summary>
    internal static string? String(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.String
            && element.GetString() is { Length: > 0 } text
                ? text
                : null;

    /// <summary>The strings in array <paramref name="property"/> of <paramref name="value"/>; empty when there's none.</summary>
    internal static IReadOnlyList<string> Strings(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(property, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => item.Length > 0)];
    }
}

/// <summary>
/// The asks an adapter put to the user and hasn't heard back on, with what it needs to answer each: the harness's
/// session for it, when that's a subagent's. Thread-safe: asks arrive on the event loop and answers on requests.
/// </summary>
internal sealed class PendingPermissionAsks
{
    private readonly ConcurrentDictionary<string, Entry> _asks = new(StringComparer.Ordinal);

    /// <summary>One waiting ask and the harness session that asked it.</summary>
    internal sealed record Entry(PermissionAsk Ask, string HarnessSessionId, string? FleetSessionId);

    /// <summary>Whether any ask still waits.</summary>
    internal bool Any => !_asks.IsEmpty;

    /// <summary>Remembers <paramref name="ask"/>; false when it's already waiting (a harness can send an ask twice).</summary>
    internal bool Add(PermissionAsk ask, string harnessSessionId, string? fleetSessionId = null)
        => _asks.TryAdd(ask.Id, new Entry(ask, harnessSessionId, fleetSessionId));

    /// <summary>The waiting ask <paramref name="id"/>, or <see langword="null"/>.</summary>
    internal Entry? Get(string id) => _asks.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>Forgets ask <paramref name="id"/>, returning it when it was waiting.</summary>
    internal Entry? Remove(string id) => _asks.TryRemove(id, out var entry) ? entry : null;

    /// <summary>Forgets every waiting ask, returning them: the harness went away, so nothing will answer them.</summary>
    internal IReadOnlyList<Entry> Clear()
    {
        var entries = _asks.Values.ToList();
        _asks.Clear();
        return entries;
    }
}

/// <summary>
/// Decides a session's asks the same way for every harness: the policy's level first, then what the user said not to
/// ask again about in this session. Fleet keeps those rules itself, rather than answering the harness "always": OpenCode
/// would keep that for every session in the folder, and Claude Code forgets it when its process ends after each prompt.
/// </summary>
internal sealed class PermissionGate
{
    private readonly List<(string Tool, string Pattern)> _allowed = [];
    private IPermissionRules? _rules;

    /// <summary>What the session may do without asking; everything until Fleet says otherwise.</summary>
    public PermissionPolicy Policy { get; set; } = PermissionPolicy.AllowAll;

    /// <summary>The asks waiting on the user.</summary>
    public PendingPermissionAsks Pending { get; } = new();

    /// <summary>
    /// The answer Fleet gives an ask for <paramref name="tool"/> on <paramref name="patterns"/> without the user:
    /// <see cref="PermissionReplies.Once"/> or <see cref="PermissionReplies.Reject"/>, or <see langword="null"/> when
    /// the user has to answer.
    /// </summary>
    public string? Decide(string tool, IReadOnlyList<string> patterns)
    {
        var decision = Policy.Decide(PermissionKinds.Classify(tool));
        if (decision != PermissionReplies.Reject && decision is not null)
            return decision;
        if (AllowedBefore(tool, patterns))
            return PermissionReplies.Once;
        return decision;
    }

    /// <summary>
    /// Uses the rules Fleet keeps for the session in its database, so they outlast this gate and can be listed and
    /// removed. Without them (a harness session no one bound) the gate remembers in memory only.
    /// </summary>
    public async Task BindAsync(IPermissionRules rules, CancellationToken ct)
    {
        await rules.LoadAsync(ct).ConfigureAwait(false);
        List<(string Tool, string Pattern)> clickedBefore;
        lock (_allowed)
        {
            clickedBefore = [.. _allowed];
            _allowed.Clear();
            _rules = rules;
        }

        // What was clicked before the rules were bound is kept in them.
        foreach (var group in clickedBefore.GroupBy(rule => rule.Tool, StringComparer.OrdinalIgnoreCase))
            await rules.AddAsync(group.Key, [.. group.Select(rule => rule.Pattern)], ct).ConfigureAwait(false);
    }

    /// <summary>Remembers "Don't ask again" for <paramref name="ask"/>: its tool on its patterns, for the rest of the session.</summary>
    public async Task AllowFromNowOnAsync(PermissionAsk ask, CancellationToken ct)
    {
        var patterns = ask.Always.Count > 0 ? ask.Always : ["*"];
        IPermissionRules? rules;
        lock (_allowed)
        {
            rules = _rules;
            if (rules is null)
            {
                foreach (var pattern in patterns)
                    _allowed.Add((ask.Tool, pattern));
            }
        }

        if (rules is not null)
            await rules.AddAsync(ask.Tool, patterns, ct).ConfigureAwait(false);
    }

    private bool AllowedBefore(string tool, IReadOnlyList<string> patterns)
    {
        List<(string Tool, string Pattern)> all;
        lock (_allowed)
        {
            all = _rules is { } rules
                ? [.. rules.Current.SelectMany(rule => rule.Patterns.Select(pattern => (rule.Tool, pattern)))]
                : [.. _allowed];
        }

        var matching = all.Where(rule => string.Equals(rule.Tool, tool, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matching.Count == 0)
            return false;
        if (patterns.Count == 0)
            return matching.Any(rule => rule.Pattern == "*");
        return patterns.All(pattern => matching.Any(rule => Wildcard.Matches(pattern, rule.Pattern)));
    }
}

/// <summary>OpenCode's wildcard patterns: <c>*</c> is any text, <c>?</c> one character; <c>git push *</c> also matches <c>git push</c>.</summary>
internal static class Wildcard
{
    public static bool Matches(string value, string pattern)
    {
        if (pattern == "*")
            return true;
        if (Regex(pattern).IsMatch(value))
            return true;
        return pattern.EndsWith(" *", StringComparison.Ordinal) && Regex(pattern[..^2]).IsMatch(value);
    }

    private static System.Text.RegularExpressions.Regex Regex(string pattern)
    {
        var expression = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal);
        return new System.Text.RegularExpressions.Regex(
            $"^{expression}$",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }
}
