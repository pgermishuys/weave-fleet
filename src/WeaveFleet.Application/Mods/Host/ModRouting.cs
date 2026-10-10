using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>A loaded mod as routing sees it.</summary>
/// <param name="Id"><c>name@v3</c> or <c>name@draft:{sessionId}</c>.</param>
/// <param name="DraftSessionId">The draft's session; null for a kept mod.</param>
/// <param name="Hooks">What its <c>register</c> registered (<c>load</c>'s <c>hooks</c>), in order.</param>
public sealed record ModRoute(string Id, string Name, string? DraftSessionId, IReadOnlyList<ModHookSpec> Hooks);

/// <summary>
/// Which mods an event goes to (<c>docs/mods/api.md</c>, "Order and failure" and "The protocol"): only mods with a hook
/// for it cross the pipe, and a draft's hooks only ever get its own session's events (draft scope A).
/// </summary>
public static class ModRouting
{
    /// <summary>
    /// The chain for <paramref name="event"/> in <paramref name="sessionId"/>, outermost first: kept mods by name
    /// (ordinal), then that session's drafts by name; a draft takes the place of the kept mod with its name in its
    /// session; other sessions' drafts are never in it. Narrowed to mods with a hook for the event whose matcher matches
    /// <paramref name="e"/>. For control events (<c>ui.press</c>, <c>ui.input</c>, <c>ui.select</c>), whose <c>e</c>
    /// Fleet sees before the host fills <c>mod</c> and <c>element</c> from the handle, any hook for the event counts.
    /// </summary>
    public static IReadOnlyList<string> ChainFor(IEnumerable<ModRoute> loaded, string @event, string sessionId, JsonElement e)
    {
        var kept = new List<ModRoute>();
        var drafts = new List<ModRoute>();
        foreach (var mod in loaded)
        {
            if (mod.DraftSessionId is null) kept.Add(mod);
            else if (string.Equals(mod.DraftSessionId, sessionId, StringComparison.Ordinal)) drafts.Add(mod);
        }

        // A draft takes the kept mod's place before anything is narrowed: a draft with no hook for this event still
        // removes the kept mod it replaces, as the host's chain would not have it either.
        var draftNames = new HashSet<string>(drafts.Select(d => d.Name), StringComparer.Ordinal);
        kept.RemoveAll(k => draftNames.Contains(k.Name));

        kept.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        drafts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var control = ControlEvents.Contains(@event);
        var chain = new List<string>();
        foreach (var mod in kept.Concat(drafts))
        {
            foreach (var hook in mod.Hooks)
            {
                if (!string.Equals(hook.Event, @event, StringComparison.Ordinal)) continue;
                // A control event's e still carries the handle: the host fills mod and element from it, so a matcher
                // on them can't be judged here. Send it on and let the host apply the matcher.
                if (control || Matches(hook.Matcher, e))
                {
                    chain.Add(mod.Id);
                    break;
                }
            }
        }

        return chain;
    }

    /// <summary>
    /// The host's matcher rule: no matcher matches everything; otherwise <paramref name="e"/> must be an object and every
    /// matcher field must be present in it and match: an array matches when any element does, <c>{ "$regex", "flags" }</c>
    /// matches a string by the JavaScript pattern, an object matches a nested object the same way, and anything else
    /// matches an equal JSON value. A pattern .NET can't run the same way counts as a match (the host decides).
    /// </summary>
    public static bool Matches(JsonElement? matcher, JsonElement e)
    {
        if (matcher is not { } m || m.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        return ObjectMatches(m, e);
    }

    private static readonly HashSet<string> ControlEvents = new(StringComparer.Ordinal) { "ui.press", "ui.input", "ui.select" };

    private const int MaxCachedRegexes = 256;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<(string Source, string Flags), Regex?> RegexCache = new();

    private static bool ObjectMatches(JsonElement matcher, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (matcher.ValueKind != JsonValueKind.Object) return false;
        foreach (var field in matcher.EnumerateObject())
        {
            if (!e.TryGetProperty(field.Name, out var have)) return false;
            if (!ValueMatches(field.Value, have)) return false;
        }

        return true;
    }

    private static bool ValueMatches(JsonElement want, JsonElement have)
    {
        switch (want.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in want.EnumerateArray())
                {
                    if (ValueMatches(item, have)) return true;
                }

                return false;
            case JsonValueKind.Object:
                if (want.TryGetProperty("$regex", out var source) && source.ValueKind == JsonValueKind.String)
                {
                    if (have.ValueKind != JsonValueKind.String) return false;
                    var flags = want.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
                    return RegexMatches(source.GetString() ?? "", flags, have.GetString() ?? "");
                }

                return have.ValueKind == JsonValueKind.Object && ObjectMatches(want, have);
            case JsonValueKind.String:
                return have.ValueKind == JsonValueKind.String && have.ValueEquals(want.GetString());
            case JsonValueKind.Number:
                return have.ValueKind == JsonValueKind.Number && want.GetDouble() == have.GetDouble();
            default:
                // true, false and null: equal when the kind is.
                return want.ValueKind == have.ValueKind;
        }
    }

    private static bool RegexMatches(string source, string flags, string input)
    {
        if (RegexCache.Count > MaxCachedRegexes) RegexCache.Clear();
        var regex = RegexCache.GetOrAdd((source, flags), static key => Build(key.Source, key.Flags));

        // A pattern .NET can't build or finish in time counts as a match. Being inclusive is safe: the host applies its
        // own matcher again; dropping an event the host would have run is not.
        if (regex is null) return true;
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static Regex? Build(string source, string flags)
    {
        // JavaScript flags: g, y, d, u and v don't change whether a string matches (the host resets lastIndex).
        var options = RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        try
        {
            // Interpreted on purpose: Native AOT has no compiler for RegexOptions.Compiled.
            return new Regex(source, options, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
