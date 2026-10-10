using System.Text.Json;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>A loaded mod as routing sees it.</summary>
/// <param name="Id"><c>name@v3</c> or <c>name@draft:{sessionId}</c>.</param>
/// <param name="DraftSessionId">The draft's session; null for a kept mod.</param>
/// <param name="Hooks">What its <c>register</c> registered (<c>load</c>'s <c>hooks</c>), in order.</param>
public sealed record ModRoute(string Id, string Name, string? DraftSessionId, IReadOnlyList<ModHookSpec> Hooks);

/// <param name="Matcher">The matcher as JSON, a RegExp as <c>{ "$regex": source, "flags": flags }</c>; null for none.</param>
public sealed record ModHookSpec(string Event, JsonElement? Matcher);

/// <summary>
/// Which mods an event goes to (<c>docs/mods/api.md</c>, "Order and failure"): only mods with a hook for it cross the
/// pipe, and a draft's hooks only ever get its own session's events (draft scope A). The host applies the exact matchers.
/// </summary>
public static class ModRouting
{
    /// <summary>
    /// The chain for <paramref name="event"/> in <paramref name="sessionId"/>, outermost first: kept mods by name
    /// (ordinal), then that session's drafts by name; a draft takes the place of the kept mod with its name in its session;
    /// other sessions' drafts are never in it. Narrowed to mods with a hook for the event whose matcher passes
    /// <see cref="MayMatch"/>.
    /// </summary>
    public static IReadOnlyList<string> ChainFor(IEnumerable<ModRoute> loaded, string @event, string sessionId, JsonElement e)
    {
        var kept = new List<ModRoute>();
        var drafts = new List<ModRoute>();
        foreach (var mod in loaded)
        {
            if (mod.DraftSessionId is null)
                kept.Add(mod);
            else if (string.Equals(mod.DraftSessionId, sessionId, StringComparison.Ordinal))
                drafts.Add(mod);
        }

        // A draft takes the kept mod's place before anything is narrowed, as in the host's own chain.
        var draftNames = new HashSet<string>(drafts.Select(d => d.Name), StringComparer.Ordinal);
        kept.RemoveAll(k => draftNames.Contains(k.Name));
        kept.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        drafts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var control = @event is "ui.press" or "ui.input" or "ui.select";
        return [.. kept.Concat(drafts)
            .Where(mod => mod.Hooks.Any(h => string.Equals(h.Event, @event, StringComparison.Ordinal) && (control || MayMatch(h.Matcher, e))))
            .Select(mod => mod.Id)];
    }

    /// <summary>
    /// A coarse pre-filter, never stricter than the host: no matcher passes; each matcher field must be present in
    /// <paramref name="e"/>; a literal value must equal it (strings ordinal, numbers by value); an array passes when any
    /// element does; a nested object is checked the same way; a <c>{ "$regex", … }</c> object passes for any string
    /// (the host decides). Control events (<c>ui.press</c>, <c>ui.input</c>, <c>ui.select</c>) pass any hook, since
    /// Fleet sees their <c>e</c> before the host fills <c>mod</c> and <c>element</c>.
    /// </summary>
    public static bool MayMatch(JsonElement? matcher, JsonElement e)
        => matcher is not { } m || m.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || ObjectMayMatch(m, e);

    private static bool ObjectMayMatch(JsonElement matcher, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || matcher.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var field in matcher.EnumerateObject())
        {
            if (!e.TryGetProperty(field.Name, out var have) || !ValueMayMatch(field.Value, have))
                return false;
        }

        return true;
    }

    private static bool ValueMayMatch(JsonElement want, JsonElement have)
    {
        switch (want.ValueKind)
        {
            case JsonValueKind.Array:
                return want.EnumerateArray().Any(item => ValueMayMatch(item, have));
            case JsonValueKind.Object:
                // No .NET Regex: JavaScript and .NET disagree on patterns, so any string passes and the host decides.
                if (want.TryGetProperty("$regex", out _))
                    return have.ValueKind == JsonValueKind.String;
                return ObjectMayMatch(want, have);
            case JsonValueKind.String:
                return have.ValueKind == JsonValueKind.String && have.ValueEquals(want.GetString());
            case JsonValueKind.Number:
                return have.ValueKind == JsonValueKind.Number && want.GetDouble() == have.GetDouble();
            default:
                return want.ValueKind == have.ValueKind;
        }
    }
}
