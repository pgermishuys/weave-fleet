using System.Text.Json;

namespace WeaveFleet.Application.Browser;

/// <summary>
/// What an agent can do in its own browser tabs. The names are OpenCode 2's browser plugin's operation names, which
/// every harness's tools map onto; Fleet supplies the browser, so the same tabs, limits and steps serve them all.
/// </summary>
public static class AgentBrowserKinds
{
    public const string TabsList = "tabs.list";
    public const string TabsOpen = "tabs.open";
    public const string TabsFocus = "tabs.focus";
    public const string TabsClose = "tabs.close";
    public const string Navigate = "navigate";
    public const string Back = "back";
    public const string Forward = "forward";
    public const string Reload = "reload";
    public const string Stop = "stop";
    public const string Frames = "frames";
    public const string Snapshot = "snapshot";
    public const string Find = "find";
    public const string Evaluate = "evaluate";
    public const string Click = "click";
    public const string Hover = "hover";
    public const string Drag = "drag";
    public const string Fill = "fill";
    public const string FillForm = "fill_form";
    public const string Select = "select";
    public const string Check = "check";
    public const string Press = "press";
    public const string Scroll = "scroll";
    public const string Wait = "wait";
    public const string Screenshot = "screenshot";
    public const string Dialog = "dialog";
    public const string Console = "console";
    public const string NetworkList = "network.list";
    public const string NetworkGet = "network.get";

    /// <summary>Actions that need an element ref from the tab's latest snapshot.</summary>
    public static bool TakesRef(string kind) => kind is Click or Hover or Fill or Select or Check;
}

/// <summary>A field of <see cref="AgentBrowserKinds.FillForm"/>: <c>text</c>, <c>select</c> or <c>check</c>.</summary>
public sealed record AgentFormField(string Ref, string Type, string? Value = null, IReadOnlyList<string>? Values = null, bool? Checked = null);

/// <summary>
/// One action in an agent's browser. <see cref="Kind"/> is one of <see cref="AgentBrowserKinds"/>; the other fields are
/// what that kind takes, and the rest stay null.
/// </summary>
public sealed record AgentBrowserAction(string Kind)
{
    public string? TabId { get; init; }
    public string? Url { get; init; }
    public bool? Focus { get; init; }
    public string? Ref { get; init; }
    public string? FromRef { get; init; }
    public string? ToRef { get; init; }
    public string? Text { get; init; }
    public string? Key { get; init; }
    public string? Script { get; init; }
    public string? Button { get; init; }
    public int? Count { get; init; }
    public IReadOnlyList<string>? Modifiers { get; init; }
    public IReadOnlyList<string>? Values { get; init; }
    public bool? Checked { get; init; }
    public IReadOnlyList<AgentFormField>? Fields { get; init; }
    public int? DeltaX { get; init; }
    public int? DeltaY { get; init; }
    public string? Condition { get; init; }
    public int? TimeoutMs { get; init; }
    public int? Depth { get; init; }
    public bool? FullPage { get; init; }
    public string? Format { get; init; }
    public int? Quality { get; init; }
    public int? MaxWidth { get; init; }
    public string? DialogAction { get; init; }
    public string? PromptText { get; init; }
    public string? Level { get; init; }
    public int? Limit { get; init; }
    public string? UrlContains { get; init; }
    public string? ResourceType { get; init; }
    public string? RequestId { get; init; }
    public bool? IncludeBody { get; init; }
    public int? MaxBodyChars { get; init; }
}

/// <summary>A tab as the agent and the user see it.</summary>
public sealed record AgentTab(
    string Id,
    string Url,
    string Title,
    bool Loading,
    string? LoadError,
    bool CanGoBack,
    bool CanGoForward,
    int Generation);

public sealed record AgentConsoleEntry(string Id, double TimestampMs, string Level, string Text, bool TextTruncated, string? SourceUrl, int? Line, int? Column);

public sealed record AgentNetworkRequest(
    string Id,
    string Url,
    string Method,
    string ResourceType,
    double TimestampMs,
    int? StatusCode,
    string State,
    double? DurationMs,
    string? Failure);

public sealed record AgentHeader(string Name, string Value);

/// <summary>A body of a request or response: <c>notRequested</c>, <c>pending</c>, <c>empty</c>, <c>text</c> or <c>unavailable</c>.</summary>
public sealed record AgentBody(string State, string? Text = null, bool Truncated = false, string? Reason = null);

public sealed record AgentNetworkDetail(
    AgentNetworkRequest Request,
    IReadOnlyList<AgentHeader> RequestHeaders,
    IReadOnlyList<AgentHeader> ResponseHeaders,
    AgentBody RequestBody,
    AgentBody ResponseBody);

public sealed record AgentDialog(string Type, string Message, string DefaultValue);

public sealed record AgentFrame(string Id, string? ParentId, string Url, string Name);

/// <summary>Where an element was on the tab's screen when the agent acted on it, for the ring in Agent's view.</summary>
public sealed record AgentBox(double X, double Y, double Width, double Height);

/// <summary>Why an action didn't run, in words the agent can act on. <see cref="Code"/> is one of <see cref="AgentBrowserFailures"/>.</summary>
public sealed record AgentBrowserFailure(string Code, string Message);

public static class AgentBrowserFailures
{
    /// <summary>The browser is turned off in Settings → Browser.</summary>
    public const string Off = "off";

    /// <summary>A setting forbids it: an address outside the allowed pages, a script while scripts are off.</summary>
    public const string Denied = "denied";

    /// <summary>Fleet doesn't do this operation.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The tab is closed or not this session's.</summary>
    public const string TabUnavailable = "tab_unavailable";

    /// <summary>No browser to drive (none installed, or it failed to start).</summary>
    public const string NoBrowser = "no_browser";

    /// <summary>The action ran and failed, or couldn't run.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// What an action gave back. Which fields are set depends on the kind: every action on a tab sets
/// <see cref="Tab"/>; <see cref="Failure"/> is set instead when it didn't run.
/// </summary>
public sealed record AgentBrowserResult
{
    public AgentBrowserFailure? Failure { get; init; }
    public AgentTab? Tab { get; init; }
    public IReadOnlyList<AgentTab>? Tabs { get; init; }
    public string? FocusedTabId { get; init; }
    public string? Content { get; init; }
    public bool Truncated { get; init; }
    public JsonElement? Value { get; init; }
    public byte[]? Image { get; init; }
    public string? ImageMime { get; init; }
    public IReadOnlyList<AgentConsoleEntry>? Console { get; init; }
    public IReadOnlyList<AgentNetworkRequest>? Requests { get; init; }
    public AgentNetworkDetail? Request { get; init; }
    public int Dropped { get; init; }
    public AgentDialog? Dialog { get; init; }
    public IReadOnlyList<AgentFrame>? Frames { get; init; }

    public bool Ok => Failure is null;

    public static AgentBrowserResult Fail(string code, string message) => new() { Failure = new AgentBrowserFailure(code, message) };
}

/// <summary>One call: whose session, and what to do.</summary>
public sealed record AgentBrowserCall(string SessionId, string UserId, AgentBrowserAction Action);

/// <summary>
/// The agent's own browser: tabs per session in Fleet's headless browser, kept to the pages Settings → Browser allows.
/// The user's view of a page is theirs; the agent's tabs are separate, with their own cookies and sign-ins.
/// </summary>
public interface IAgentBrowser
{
    Task<AgentBrowserResult> RunAsync(AgentBrowserCall request, CancellationToken ct = default);

    /// <summary>The session's open tabs and the focused one.</summary>
    (IReadOnlyList<AgentTab> Tabs, string? FocusedTabId) TabsOf(string sessionId);

    /// <summary>A JPEG of what the tab shows now, for Agent's view; null when the tab is gone.</summary>
    Task<byte[]?> FrameAsync(string sessionId, string tabId, CancellationToken ct = default);

    /// <summary>Closes the session's tabs, e.g. when the session goes away.</summary>
    Task CloseSessionAsync(string sessionId);
}

/// <summary>Which pages an agent's tab may open (Settings → Browser).</summary>
public static class AgentBrowserPages
{
    /// <summary>The session's own pages: apps it started and pages its canvases show. The default.</summary>
    public const string Session = "session";

    /// <summary>Any page on this machine (a loopback address).</summary>
    public const string Machine = "machine";

    /// <summary>Any http or https address.</summary>
    public const string Any = "any";

    public static bool IsKnown(string? value) => value is Session or Machine or Any;
}

/// <summary>The user's choices in Settings → Browser, with the defaults the user agreed: on, own pages, no scripts.</summary>
public sealed record AgentBrowserSettings(bool Enabled = true, string Pages = AgentBrowserPages.Session, bool Scripts = false)
{
    public const string EnabledKey = "AgentBrowser.Enabled";
    public const string PagesKey = "AgentBrowser.Pages";
    public const string ScriptsKey = "AgentBrowser.Scripts";

    public static AgentBrowserSettings From(IReadOnlyDictionary<string, string> preferences)
        => new(
            Enabled: !preferences.TryGetValue(EnabledKey, out var enabled) || !bool.TryParse(enabled, out var on) || on,
            Pages: preferences.TryGetValue(PagesKey, out var pages) && AgentBrowserPages.IsKnown(pages) ? pages : AgentBrowserPages.Session,
            Scripts: preferences.TryGetValue(ScriptsKey, out var scripts) && bool.TryParse(scripts, out var allowed) && allowed);
}
