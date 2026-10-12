using System.Text.Json;

namespace WeaveFleet.Application.Mods.Host;

/// <summary>A loaded mod as routing sees it.</summary>
/// <param name="Id"><c>name@v3</c> or <c>name@draft:{sessionId}</c>.</param>
/// <param name="DraftSessionId">The draft's session; null for a kept mod.</param>
/// <param name="Hooks">What its <c>register</c> registered (<c>load</c>'s <c>hooks</c>), in order.</param>
public sealed record ModRoute(string Id, string Name, string? DraftSessionId, IReadOnlyList<ModHookSpec> Hooks, IReadOnlyList<ModPaneSpec>? Panes = null);

/// <summary>A pane <c>mod.json</c> declares (<c>panes</c>), listed in the session's "+" menu.</summary>
public sealed record ModPaneSpec(string Id, string Title, string? Icon);

/// <summary>A declared pane for the browser: whose it is, whether it is the session's draft, and whether its mod is off.</summary>
public sealed record ModDeclaredPane(string Mod, string Id, string Title, string? Icon, bool Draft, bool Off);

/// <summary>A render site and the tools a session's mods may draw there; <see cref="Tools"/> is null when any tool may be drawn on.</summary>
public sealed record ModSiteHooks(string Site, IReadOnlyList<string>? Tools);

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
        var control = @event is "ui.press" or "ui.input" or "ui.select";
        return [.. InSession(loaded, sessionId)
            .Where(mod => mod.Hooks.Any(h => string.Equals(h.Event, @event, StringComparison.Ordinal) && (control || MayMatch(h.Matcher, e))))
            .Select(mod => mod.Id)];
    }

    /// <summary>
    /// The tool-row sites <paramref name="loaded"/> mods may draw in the session: for each of <c>ToolUse</c> and
    /// <c>ToolResult</c> that a <c>ui.render</c> hook may match, the tool names its matchers allow, or null when any tool
    /// may match (no <c>props.tool</c>, or a regular expression). Sites no hook may match are left out.
    /// </summary>
    public static IReadOnlyList<ModSiteHooks> SitesFor(IEnumerable<ModRoute> loaded, string sessionId)
    {
        var sites = new Dictionary<string, (bool Any, List<string> Tools)>(StringComparer.Ordinal);
        foreach (var mod in InSession(loaded, sessionId))
        {
            foreach (var hook in mod.Hooks.Where(h => h.Event == "ui.render"))
            {
                var tools = ToolsOf(hook.Matcher);
                foreach (var site in ToolSites.Where(s => ComponentMayMatch(hook.Matcher, s)))
                {
                    var (any, names) = sites.GetValueOrDefault(site, (false, []));
                    if (tools is null)
                        any = true;
                    else
                        names.AddRange(tools.Where(t => !names.Contains(t)));
                    sites[site] = (any, names);
                }
            }
        }

        return [.. ToolSites.Where(sites.ContainsKey).Select(s => new ModSiteHooks(s, sites[s].Any ? null : sites[s].Tools))];
    }

    /// <summary>The panes the session's loaded mods declare, in load order (kept mods, then the session's drafts).</summary>
    public static IReadOnlyList<ModDeclaredPane> PanesFor(IEnumerable<ModRoute> loaded, string sessionId)
        => [.. InSession(loaded, sessionId).SelectMany(mod => (mod.Panes ?? []).Select(p => new ModDeclaredPane(mod.Name, p.Id, p.Title, p.Icon, mod.DraftSessionId is not null, Off: false)))];

    private static readonly string[] ToolSites = ["ToolUse", "ToolResult"];

    /// <summary>A matcher's <c>component</c> may be a name, a list of names or anything else (a regular expression: any).</summary>
    private static bool ComponentMayMatch(JsonElement? matcher, string site)
    {
        if (matcher is not { ValueKind: JsonValueKind.Object } m || !m.TryGetProperty("component", out var component))
            return true;
        return component.ValueKind switch
        {
            JsonValueKind.String => component.ValueEquals(site),
            JsonValueKind.Array => component.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String || c.ValueEquals(site)),
            _ => true,
        };
    }

    /// <summary>The tool names a matcher allows; null for any tool.</summary>
    private static List<string>? ToolsOf(JsonElement? matcher)
    {
        if (matcher is not { ValueKind: JsonValueKind.Object } m
            || !m.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object
            || !props.TryGetProperty("tool", out var tool))
            return null;
        if (tool.ValueKind == JsonValueKind.String)
            return [tool.GetString()!];
        if (tool.ValueKind != JsonValueKind.Array || tool.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
            return null;
        return [.. tool.EnumerateArray().Select(t => t.GetString()!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The mods that apply in the session, outermost first: kept mods by name (ordinal), then that session's drafts by
    /// name; a draft takes the place of the kept mod with its name; other sessions' drafts are never in it.
    /// </summary>
    private static IEnumerable<ModRoute> InSession(IEnumerable<ModRoute> loaded, string sessionId)
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
        return kept.Concat(drafts);
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
