using System.Text.Json;

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
        => throw new NotImplementedException();

    /// <summary>
    /// The host's matcher rule: no matcher matches everything; otherwise <paramref name="e"/> must be an object and every
    /// matcher field must be present in it and match: an array matches when any element does, <c>{ "$regex", "flags" }</c>
    /// matches a string by the JavaScript pattern, an object matches a nested object the same way, and anything else
    /// matches an equal JSON value. A pattern .NET can't run the same way counts as a match (the host decides).
    /// </summary>
    public static bool Matches(JsonElement? matcher, JsonElement e) => throw new NotImplementedException();
}
