using System.Text;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// One action an agent took in its browser, as the conversation and Agent's view show it. Fleet knows each one
/// because it ran it, whatever the harness: OpenCode 2 sends many in one Code Mode script, OpenCode one per tool call.
/// </summary>
public sealed record AgentBrowserStep
{
    public required string SessionId { get; init; }

    /// <summary>Its place in the session's steps, from 1; set when it's recorded.</summary>
    public long Seq { get; init; }

    public required DateTimeOffset At { get; init; }
    public required string Kind { get; init; }

    /// <summary>What happened in plain words, e.g. <c>Clicked “Save”</c>.</summary>
    public required string Summary { get; init; }

    /// <summary>The operation and how long it took, e.g. <c>click @e2 · 170 ms</c>.</summary>
    public required string Detail { get; init; }

    public bool Ok { get; init; } = true;
    public string? Error { get; init; }
    public string? TabId { get; init; }
    public string? Url { get; init; }
    public string? Title { get; init; }

    /// <summary>The element acted on, where it was on the tab's screen, for the ring in Agent's view.</summary>
    public AgentBox? Box { get; init; }

    /// <summary>A screenshot the step took, kept for the conversation.</summary>
    public ScreenshotReference? Screenshot { get; init; }
}

/// <summary>Keeps a session's browser steps and tells the session's watchers about each one (<c>browser.step</c>).</summary>
public interface IAgentBrowserSteps
{
    Task<AgentBrowserStep> RecordAsync(AgentBrowserStep entry, string userId, CancellationToken ct = default);

    /// <summary>The session's steps, oldest first, the newest <paramref name="limit"/> of them.</summary>
    Task<IReadOnlyList<AgentBrowserStep>> ListAsync(string sessionId, int limit = 500, CancellationToken ct = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>The words for a step, from the action, what it acted on and how it went.</summary>
public static class AgentBrowserStepText
{
    /// <param name="target">The element acted on, already in words: <c>“Save”</c>, or <c>the button</c> when it has no name.</param>
    /// <param name="facts">A short fact the action found out, e.g. a count; appended after a colon.</param>
    public static string Summary(AgentBrowserAction action, string? target, string? facts, AgentTab? tab)
    {
        var name = target is { Length: > 0 } ? target : "an element";
        var text = action.Kind switch
        {
            AgentBrowserKinds.TabsOpen => "Opened " + Place(tab?.Url ?? action.Url) + " in its own tab",
            AgentBrowserKinds.TabsFocus => "Showed its tab",
            AgentBrowserKinds.TabsClose => "Closed its tab",
            AgentBrowserKinds.TabsList => "Listed its tabs",
            AgentBrowserKinds.Navigate => "Went to " + Place(tab?.Url ?? action.Url),
            AgentBrowserKinds.Back => "Went back",
            AgentBrowserKinds.Forward => "Went forward",
            AgentBrowserKinds.Reload => "Reloaded the page",
            AgentBrowserKinds.Stop => "Stopped loading",
            AgentBrowserKinds.Frames => "Listed the page's frames",
            AgentBrowserKinds.Snapshot => "Read the page",
            AgentBrowserKinds.Find => "Looked for " + Quote(action.Text ?? string.Empty),
            AgentBrowserKinds.Evaluate => "Ran a script in the page",
            AgentBrowserKinds.Click => (action.Count == 2 ? "Double-clicked " : action.Button == "right" ? "Right-clicked " : "Clicked ") + name,
            AgentBrowserKinds.Hover => "Pointed at " + name,
            AgentBrowserKinds.Drag => "Dragged " + name,
            AgentBrowserKinds.Fill => string.IsNullOrEmpty(action.Text) ? "Cleared " + name : "Typed " + Quote(Short(action.Text)) + " in " + name,
            AgentBrowserKinds.FillForm => $"Filled {action.Fields?.Count ?? 0} field{(action.Fields?.Count == 1 ? "" : "s")}",
            AgentBrowserKinds.Select => "Picked " + Quote(string.Join(", ", action.Values ?? [])) + " in " + name,
            AgentBrowserKinds.Check => (action.Checked == false ? "Unchecked " : "Checked ") + name,
            AgentBrowserKinds.Press => "Pressed " + (action.Key ?? "a key"),
            AgentBrowserKinds.Scroll => (action.DeltaY ?? 0) < 0 ? "Scrolled up" : "Scrolled down",
            AgentBrowserKinds.Wait => action.Condition switch
            {
                "text" => "Waited for " + Quote(action.Text ?? string.Empty),
                "textGone" => "Waited for " + Quote(action.Text ?? string.Empty) + " to go",
                _ => "Waited for the page to load",
            },
            AgentBrowserKinds.Screenshot => "Took a screenshot",
            AgentBrowserKinds.Dialog => action.DialogAction switch
            {
                "accept" => "Accepted the dialog",
                "dismiss" => "Dismissed the dialog",
                _ => "Checked for a dialog",
            },
            AgentBrowserKinds.Console => "Read the console",
            AgentBrowserKinds.NetworkList => "Read the page's requests",
            AgentBrowserKinds.NetworkGet => "Read a request",
            _ => "Used the browser (" + action.Kind + ")",
        };
        return facts is { Length: > 0 } ? text + ": " + facts : text;
    }

    /// <summary><c>click @e2 · 170 ms</c>: the operation, its ref or key, and how long it took.</summary>
    public static string Detail(AgentBrowserAction action, TimeSpan took)
    {
        var detail = new StringBuilder(action.Kind);
        var argument = action.Ref ?? action.FromRef ?? action.Key;
        if (argument is { Length: > 0 })
            detail.Append(' ').Append(argument.StartsWith('@') || action.Key is not null ? argument : "@" + argument);
        return detail.Append(" · ").Append(Math.Max(1, (int)Math.Round(took.TotalMilliseconds))).Append(" ms").ToString();
    }

    private static string Quote(string text) => "“" + text + "”";

    private static string Short(string text) => text.Length <= 40 ? text : text[..39] + "…";

    /// <summary>A page's address without the scheme, short enough for a step.</summary>
    private static string Place(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "a blank page";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return url;
        var place = uri.Authority + (uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery);
        return place.Length <= 60 ? place : place[..59] + "…";
    }
}
