using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// The agent's browser (<see cref="IAgentBrowser"/>) in Fleet's headless Chrome, over the DevTools protocol. Each
/// session has its own tabs; one action at a time per session, so a click never races the snapshot that gave it its
/// ref. A tab reads the page as an accessibility tree with element refs, takes input as real mouse and key events,
/// and keeps the console, the requests and an open dialog for the agent to read.
/// <para>
/// Settings → Browser is checked on every action and enforced here: the address an action asks for, and every page
/// the tab's main frame loads (a click on a link included), go through <c>Fetch</c>, and anything outside the allowed
/// pages is blocked. Page scripts are off unless the user allows them; uploads are refused.
/// </para>
/// <para>
/// The tabs keep the browser running (a <see cref="ChromeHost"/> lease); a session's tabs close after
/// <see cref="IdleTabs"/> without an action or anyone watching them, and the last one closed lets the browser quit.
/// </para>
/// </summary>
public sealed partial class CdpAgentBrowser : IAgentBrowser, IAsyncDisposable
{
    /// <summary>How long a session's tabs stay open without an action and without anyone watching Agent's view.</summary>
    public static readonly TimeSpan IdleTabs = TimeSpan.FromMinutes(15);

    private const int Width = 1280;
    private const int Height = 800;
    private const int MaxConsole = 500;
    private const int MaxRequests = 500;
    private const int MaxText = 100_000;
    private const int MaxLines = 500;
    private const int MaxResultJson = 512_000;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FrameCache = TimeSpan.FromMilliseconds(300);

    private readonly ChromeHost _host;
    private readonly IServiceScopeFactory _scopes;
    private readonly IBackgroundUserScope _users;
    private readonly IAgentBrowserSteps? _steps;
    private readonly ISessionScreenshotStore? _screenshots;
    private readonly AgentBrowserCalls? _calls;
    private readonly ILogger<CdpAgentBrowser> _logger;
    private readonly ConcurrentDictionary<string, SessionTabs> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly Timer _sweep;
    private ChromeHost.ChromeLease? _lease;
    private CdpConnection? _cdp;
    private IDisposable? _browserEvents;
    private bool _disposed;

    public CdpAgentBrowser(
        ChromeHost host,
        IServiceScopeFactory scopes,
        IBackgroundUserScope users,
        ILogger<CdpAgentBrowser> logger,
        IAgentBrowserSteps? steps = null,
        ISessionScreenshotStore? screenshots = null,
        AgentBrowserCalls? calls = null)
    {
        _calls = calls;
        _host = host;
        _scopes = scopes;
        _users = users;
        _logger = logger;
        _steps = steps;
        _screenshots = screenshots;
        _sweep = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>For tests: the time "now" is, so idle tabs can be closed without waiting.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>For tests: the limits to apply instead of reading them from the session's settings and pages.</summary>
    internal Func<string, AgentBrowserLimits>? LimitsOverride { get; init; }

    public (IReadOnlyList<AgentTab> Tabs, string? FocusedTabId) TabsOf(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return ([], null);
        lock (session)
            return ([.. session.Tabs.Values.Select(tab => tab.Snapshot())], session.Focused);
    }

    public async Task<AgentBrowserResult> RunAsync(AgentBrowserCall request, CancellationToken ct = default)
    {
        var call = request;
        var action = call.Action;
        var limits = await LimitsAsync(call, ct);
        if (!limits.Settings.Enabled)
        {
            return AgentBrowserResult.Fail(AgentBrowserFailures.Off,
                "The browser is turned off in Fleet (Settings → Browser), so agents can't use it. Don't retry; tell the user if you need it.");
        }

        var session = _sessions.GetOrAdd(call.SessionId, id => new SessionTabs(id, call.UserId));
        await session.Gate.WaitAsync(ct);
        var started = Stopwatch.StartNew();
        var note = new StepNote();
        AgentBrowserResult result;
        try
        {
            session.LastUsed = Clock();
            result = await DispatchAsync(session, action, limits, note, ct);
        }
        catch (CdpException error)
        {
            result = AgentBrowserResult.Fail(AgentBrowserFailures.Failed,
                $"The browser failed during {action.Kind}: {error.Message} The action may already have run: call tabs.list and read the tab before repeating it.");
        }
        catch (ElementException error)
        {
            result = AgentBrowserResult.Fail(AgentBrowserFailures.Failed, error.Message);
        }
        finally
        {
            session.Gate.Release();
        }

        started.Stop();
        if (action.Kind != AgentBrowserKinds.TabsList)
            await RecordAsync(call, action, result, note, started.Elapsed, ct);
        if (result.Ok && action.Kind is AgentBrowserKinds.TabsOpen or AgentBrowserKinds.TabsFocus && action.Focus != false && result.Tab is { } shown)
            await ShowAsync(call, shown, ct);
        return result;
    }

    private async Task<AgentBrowserLimits> LimitsAsync(AgentBrowserCall call, CancellationToken ct)
    {
        if (LimitsOverride is { } fixedLimits)
            return fixedLimits(call.SessionId);

        using (_users.Begin(call.UserId))
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AgentBrowserAccess>().LimitsAsync(call.SessionId, ct);
        }
    }

    private async Task ShowAsync(AgentBrowserCall call, AgentTab tab, CancellationToken ct)
    {
        try
        {
            using (_users.Begin(call.UserId))
            {
                await using var scope = _scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<AgentBrowserCanvas>() is { } canvas)
                    await canvas.ShowAsync(call.SessionId, tab.Url, tab.Title, ct);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogShowFailed(_logger, call.SessionId, error);
        }
    }

    private async Task<AgentBrowserResult> DispatchAsync(SessionTabs session, AgentBrowserAction action, AgentBrowserLimits limits, StepNote note, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case AgentBrowserKinds.TabsList:
                return await StateAsync(session, ct);
            case AgentBrowserKinds.TabsOpen:
                return await OpenAsync(session, action, limits, note, ct);
            case "files.upload" or "files.drop" or "files.list" or "files.get":
                return AgentBrowserResult.Fail(AgentBrowserFailures.Unsupported,
                    "Fleet's browser doesn't move files into or out of pages. Check the page another way; don't retry.");
            case "trace.start" or "trace.stop" or "trace.analyze" or "cpu.start" or "cpu.stop" or "cpu.analyze"
                or "heap.snapshot" or "heap.summary" or "heap.query" or "heap.object" or "heap.compare" or "lighthouse":
                return AgentBrowserResult.Fail(AgentBrowserFailures.Unsupported,
                    $"Fleet's browser doesn't do {action.Kind} yet. Use snapshot, console and network.list to check the page.");
            case "preview":
                return AgentBrowserResult.Fail(AgentBrowserFailures.Unsupported,
                    "Show files to the user with fleet_page_show (HTML pages) instead.");
        }

        if (string.IsNullOrEmpty(action.TabId) || !TryTab(session, action.TabId, out var tab))
        {
            return AgentBrowserResult.Fail(AgentBrowserFailures.TabUnavailable,
                "This tab is closed or isn't this session's. Call tabs.list and use an exact returned tab id; if there are none, open one with tabs.open.");
        }

        tab.Limits = limits;
        if (tab.Dialog is { } open && action.Kind is not (AgentBrowserKinds.Dialog or AgentBrowserKinds.TabsList or AgentBrowserKinds.TabsClose or AgentBrowserKinds.TabsFocus))
        {
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed,
                $"A {open.Type} dialog is open on this tab (\"{open.Message}\"). Read it with dialog get, then accept or dismiss it before anything else.");
        }

        var cdp = _cdp;
        if (cdp is null || !cdp.IsOpen)
            return AgentBrowserResult.Fail(AgentBrowserFailures.TabUnavailable, "The browser closed and took this tab with it. Open a new one with tabs.open.");

        var page = new Page(cdp, tab);
        await FrontAsync(page, ct);
        return action.Kind switch
        {
            AgentBrowserKinds.TabsFocus => Focus(session, tab),
            AgentBrowserKinds.TabsClose => await CloseAsync(session, tab, ct),
            AgentBrowserKinds.Navigate => await NavigateAsync(page, action.Url, limits, ct),
            AgentBrowserKinds.Back => await HistoryAsync(page, -1, ct),
            AgentBrowserKinds.Forward => await HistoryAsync(page, +1, ct),
            AgentBrowserKinds.Reload => await LoadAsync(page, w => w.WriteBoolean("ignoreCache", true), "Page.reload", ct),
            AgentBrowserKinds.Stop => await StopAsync(page, ct),
            AgentBrowserKinds.Frames => await FramesAsync(page, ct),
            AgentBrowserKinds.Snapshot => await SnapshotAsync(page, action, find: null, note, ct),
            AgentBrowserKinds.Find => await SnapshotAsync(page, action, action.Text ?? string.Empty, note, ct),
            AgentBrowserKinds.Evaluate => await EvaluateAsync(page, action, limits, ct),
            AgentBrowserKinds.Screenshot => await ScreenshotAsync(page, action, note, ct),
            AgentBrowserKinds.Console => await ConsoleAsync(page, action, note, ct),
            AgentBrowserKinds.NetworkList => await RequestsAsync(page, action, note, ct),
            AgentBrowserKinds.NetworkGet => await RequestAsync(page, action, ct),
            AgentBrowserKinds.Dialog => await DialogAsync(page, action, ct),
            AgentBrowserKinds.Wait => await WaitAsync(page, action, ct),
            _ => await InputAsync(page, action, note, ct),
        };
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<AgentBrowserResult> OpenAsync(SessionTabs session, AgentBrowserAction action, AgentBrowserLimits limits, StepNote note, CancellationToken ct)
    {
        Uri? target = null;
        if (!string.IsNullOrWhiteSpace(action.Url) && action.Url.Trim() != "about:blank")
        {
            if (!Uri.TryCreate(action.Url.Trim(), UriKind.Absolute, out target))
                return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, $"\"{action.Url}\" isn't an address. Use a full http or https address, e.g. http://localhost:5173/.");
            if (!limits.Allows(target))
                return AgentBrowserResult.Fail(AgentBrowserFailures.Denied, limits.Refusal(target));
        }

        var (cdp, problem) = await BrowserAsync(ct);
        if (cdp is null)
            return AgentBrowserResult.Fail(AgentBrowserFailures.NoBrowser, problem ?? ChromeFinder.NotFound);

        string targetId;
        using (var created = await cdp.SendAsync("Target.createTarget", w => w.WriteString("url", "about:blank"), ct: ct))
            targetId = created.RootElement.GetProperty("result").GetProperty("targetId").GetString()!;

        string cdpSession;
        using (var attached = await cdp.SendAsync("Target.attachToTarget", w =>
        {
            w.WriteString("targetId", targetId);
            w.WriteBoolean("flatten", true);
        }, ct: ct))
        {
            cdpSession = attached.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!;
        }

        var tab = new Tab("tab_" + Guid.NewGuid().ToString("D"), targetId, cdpSession, session.SessionId) { Limits = limits };
        tab.Subscription = cdp.Subscribe(cdpSession, (method, parameters) => OnPageEvent(cdp, tab, method, parameters));
        foreach (var domain in (string[])["Page.enable", "Runtime.enable", "DOM.enable", "Network.enable", "Log.enable"])
            (await cdp.SendAsync(domain, sessionId: cdpSession, ct: ct)).Dispose();
        (await cdp.SendAsync("Page.setLifecycleEventsEnabled", w => w.WriteBoolean("enabled", true), cdpSession, ct)).Dispose();
        // The page acts focused even when another tab is in front, as it would for a person using it.
        (await cdp.SendAsync("Emulation.setFocusEmulationEnabled", w => w.WriteBoolean("enabled", true), cdpSession, ct)).Dispose();
        (await cdp.SendAsync("Emulation.setDeviceMetricsOverride", w =>
        {
            w.WriteNumber("width", Width);
            w.WriteNumber("height", Height);
            w.WriteNumber("deviceScaleFactor", 1);
            w.WriteBoolean("mobile", false);
        }, cdpSession, ct)).Dispose();
        // Every page the main frame loads is checked against the user's settings, whatever led to it.
        (await cdp.SendAsync("Fetch.enable", w =>
        {
            w.WriteStartArray("patterns");
            w.WriteStartObject();
            w.WriteString("urlPattern", "*");
            w.WriteString("resourceType", "Document");
            w.WriteString("requestStage", "Request");
            w.WriteEndObject();
            w.WriteEndArray();
        }, cdpSession, ct)).Dispose();

        lock (session)
        {
            session.Tabs[tab.Id] = tab;
            if (action.Focus != false)
                session.Focused = tab.Id;
        }

        if (target is not null)
        {
            var navigated = await NavigateAsync(new Page(cdp, tab), target.AbsoluteUri, limits, ct);
            if (!navigated.Ok)
                return navigated with { Tab = await InfoAsync(new Page(cdp, tab), ct) };
        }

        return new AgentBrowserResult { Tab = await InfoAsync(new Page(cdp, tab), ct) };
    }

    private static AgentBrowserResult Focus(SessionTabs session, Tab tab)
    {
        lock (session)
            session.Focused = tab.Id;
        return new AgentBrowserResult { Tab = tab.Snapshot() };
    }

    private async Task<AgentBrowserResult> CloseAsync(SessionTabs session, Tab tab, CancellationToken ct)
    {
        await CloseTabAsync(session, tab);
        return await StateAsync(session, ct);
    }

    private async Task<AgentBrowserResult> StateAsync(SessionTabs session, CancellationToken ct)
    {
        Tab[] tabs;
        string? focused;
        lock (session)
        {
            tabs = [.. session.Tabs.Values];
            focused = session.Focused;
        }

        var cdp = _cdp;
        List<AgentTab> list = [];
        foreach (var tab in tabs)
            list.Add(cdp is { IsOpen: true } ? await InfoAsync(new Page(cdp, tab), ct) : tab.Snapshot());
        return new AgentBrowserResult { Tabs = list, FocusedTabId = focused };
    }

    private static bool TryTab(SessionTabs session, string id, out Tab tab)
    {
        lock (session)
        {
            if (session.Tabs.TryGetValue(id, out var found) && !found.Gone)
            {
                tab = found;
                return true;
            }
        }

        tab = null!;
        return false;
    }

    private async Task CloseTabAsync(SessionTabs session, Tab tab)
    {
        bool last;
        lock (session)
        {
            session.Tabs.Remove(tab.Id);
            if (session.Focused == tab.Id)
                session.Focused = session.Tabs.Keys.FirstOrDefault();
            last = session.Tabs.Count == 0;
        }

        tab.Gone = true;
        tab.Subscription?.Dispose();
        if (_cdp is { IsOpen: true } cdp)
        {
            try
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                (await cdp.SendAsync("Target.closeTarget", w => w.WriteString("targetId", tab.TargetId), ct: closing.Token)).Dispose();
            }
            catch (Exception error) when (error is CdpException or OperationCanceledException)
            {
                // It goes with the browser.
            }
        }

        if (last)
            await ReleaseIfUnusedAsync();
    }

    public async Task CloseSessionAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
            return;
        Tab[] tabs;
        lock (session)
            tabs = [.. session.Tabs.Values];
        foreach (var tab in tabs)
            await CloseTabAsync(session, tab);
        await ReleaseIfUnusedAsync();
    }

    private async Task<(CdpConnection? Connection, string? Problem)> BrowserAsync(CancellationToken ct)
    {
        await _browserGate.WaitAsync(ct);
        try
        {
            if (_disposed)
                return (null, "Fleet is shutting down.");
            if (_cdp is { IsOpen: true } open)
                return (open, null);

            _browserEvents?.Dispose();
            _lease?.Dispose();
            _lease = await _host.AcquireAsync(ct);
            if (_lease.Connection is not { } connection)
            {
                var problem = _lease.Problem;
                _lease = null;
                return (null, problem);
            }

            _cdp = connection;
            _browserEvents = connection.Subscribe(string.Empty, (method, parameters) => OnBrowserEvent(method, parameters));
            _ = connection.Closed.ContinueWith(_ => OnBrowserGone(connection), TaskScheduler.Default);
            return (connection, null);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    /// <summary>The browser died or was reset: every tab went with it.</summary>
    private void OnBrowserGone(CdpConnection connection)
    {
        if (!ReferenceEquals(_cdp, connection))
            return;
        foreach (var session in _sessions.Values)
        {
            lock (session)
            {
                foreach (var tab in session.Tabs.Values)
                {
                    tab.Gone = true;
                    tab.Subscription?.Dispose();
                }

                session.Tabs.Clear();
                session.Focused = null;
            }
        }

        _ = ReleaseIfUnusedAsync();
    }

    private async Task ReleaseIfUnusedAsync()
    {
        await _browserGate.WaitAsync();
        try
        {
            if (_sessions.Values.Any(session => { lock (session) return session.Tabs.Count > 0; }))
                return;
            _browserEvents?.Dispose();
            _browserEvents = null;
            _cdp = null;
            _lease?.Dispose();
            _lease = null;
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private void OnBrowserEvent(string method, JsonElement parameters)
    {
        // A tab the page closed itself (window.close()), or whose renderer crashed.
        if (method is not ("Target.detachedFromTarget" or "Target.targetCrashed"))
            return;
        var targetId = parameters.TryGetProperty("targetId", out var t) ? t.GetString() : null;
        var cdpSession = parameters.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        foreach (var session in _sessions.Values)
        {
            lock (session)
            {
                foreach (var tab in session.Tabs.Values.Where(tab => tab.TargetId == targetId || tab.CdpSession == cdpSession))
                {
                    if (method == "Target.targetCrashed")
                        tab.LoadError = "The tab crashed. Reload it, or close it and open a new one.";
                    else
                        tab.Gone = true;
                }
            }
        }
    }

    private async Task SweepAsync()
    {
        var now = Clock();
        foreach (var session in _sessions.Values)
        {
            bool idle;
            lock (session)
                idle = session.Tabs.Count > 0 && now - session.LastUsed > IdleTabs && now - session.LastWatched > IdleTabs;
            if (!idle || !await session.Gate.WaitAsync(TimeSpan.Zero))
                continue;
            try
            {
                Tab[] tabs;
                lock (session)
                    tabs = [.. session.Tabs.Values];
                foreach (var tab in tabs)
                    await CloseTabAsync(session, tab);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                LogSweepFailed(_logger, session.SessionId, error);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Brings the tab to the front of Fleet's browser before acting on it or taking its picture. Headless Chrome stops
    /// painting a tab behind another, so an input event waits on a frame that doesn't come (a click took 5 s with two
    /// sessions' tabs open) and a picture shows the page as it was. Every time: screenshots open tabs in the same browser.
    /// </summary>
    private static async Task FrontAsync(Page page, CancellationToken ct)
        => (await page.SendAsync("Page.bringToFront", null, ct)).Dispose();

    // ── Navigation ───────────────────────────────────────────────────────────────────────────────────────

    private static async Task<AgentBrowserResult> NavigateAsync(Page page, string? url, AgentBrowserLimits limits, CancellationToken ct)
    {
        var value = url?.Trim() ?? string.Empty;
        if (value.Length == 0)
            value = "about:blank";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var target))
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, $"\"{url}\" isn't an address. Use a full http or https address, e.g. http://localhost:5173/.");
        if (!limits.Allows(target))
            return AgentBrowserResult.Fail(AgentBrowserFailures.Denied, limits.Refusal(target));

        string? error = null;
        var result = await LoadAsync(page, w => w.WriteString("url", target.AbsoluteUri), "Page.navigate", ct, reply =>
        {
            if (reply.TryGetProperty("errorText", out var text) && text.GetString() is { Length: > 0 } failed)
                error = failed;
        });
        if (error is not null)
        {
            page.Tab.LoadError = error;
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, error.Contains("BLOCKED_BY_CLIENT", StringComparison.Ordinal)
                ? limits.Refusal(target)
                : $"The browser couldn't open {target}: {error}. Is the app running? Check with fleet_canvas_read.") with { Tab = await InfoAsync(page, ct) };
        }

        return result;
    }

    private static async Task<AgentBrowserResult> LoadAsync(Page page, Action<Utf8JsonWriter>? parameters, string method, CancellationToken ct, Action<JsonElement>? reply = null)
    {
        using var loaded = page.Cdp.Expect("Page.loadEventFired", page.Tab.CdpSession);
        using (var answer = await page.SendAsync(method, parameters, ct))
            reply?.Invoke(answer.RootElement.GetProperty("result"));
        var done = await loaded.ArrivedAsync(LoadTimeout, ct);
        var tab = await InfoAsync(page, ct);
        return new AgentBrowserResult { Tab = done ? tab : tab with { Loading = true } };
    }

    private static async Task<AgentBrowserResult> HistoryAsync(Page page, int step, CancellationToken ct)
    {
        using var history = await page.SendAsync("Page.getNavigationHistory", null, ct);
        var result = history.RootElement.GetProperty("result");
        var index = result.GetProperty("currentIndex").GetInt32() + step;
        var entries = result.GetProperty("entries");
        if (index < 0 || index >= entries.GetArrayLength())
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, step < 0 ? "There's no page to go back to in this tab." : "There's no page to go forward to in this tab.");
        var entry = entries[index].GetProperty("id").GetInt32();
        return await LoadAsync(page, w => w.WriteNumber("entryId", entry), "Page.navigateToHistoryEntry", ct);
    }

    private static async Task<AgentBrowserResult> StopAsync(Page page, CancellationToken ct)
    {
        (await page.SendAsync("Page.stopLoading", null, ct)).Dispose();
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct) };
    }

    private static async Task<AgentBrowserResult> FramesAsync(Page page, CancellationToken ct)
    {
        using var tree = await page.SendAsync("Page.getFrameTree", null, ct);
        List<AgentFrame> frames = [];
        void Walk(JsonElement node)
        {
            var frame = node.GetProperty("frame");
            frames.Add(new AgentFrame(
                frame.GetProperty("id").GetString()!,
                frame.TryGetProperty("parentId", out var parent) ? parent.GetString() : null,
                frame.GetProperty("url").GetString() ?? string.Empty,
                frame.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty));
            if (node.TryGetProperty("childFrames", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child);
        }

        Walk(tree.RootElement.GetProperty("result").GetProperty("frameTree"));
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Frames = frames };
    }

    // ── Reading the page ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<AgentBrowserResult> SnapshotAsync(Page page, AgentBrowserAction action, string? find, StepNote note, CancellationToken ct)
    {
        var depth = Math.Clamp(action.Depth ?? 16, 1, 20);
        using var tree = await page.SendAsync("Accessibility.getFullAXTree", w => w.WriteNumber("depth", depth), ct);
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var node in tree.RootElement.GetProperty("result").GetProperty("nodes").EnumerateArray())
            nodes[node.GetProperty("nodeId").GetString()!] = node;
        if (nodes.Count == 0)
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "The page has no content yet. Wait for it to load (wait with condition \"load\"), then read it again.");

        var tab = page.Tab;
        JsonElement root = nodes.Values.First();
        if (action.Ref is { } start)
        {
            var element = tab.Element(start);
            root = nodes.Values.FirstOrDefault(node => node.TryGetProperty("backendDOMNodeId", out var b) && b.GetInt64() == element.BackendNodeId);
            if (root.ValueKind != JsonValueKind.Object)
                throw new ElementException("That element isn't in the page's accessibility tree any more. Read the page again (snapshot without a ref) and use a fresh ref.");
        }

        var refs = new Dictionary<string, ElementRef>(StringComparer.Ordinal);
        var lines = new List<string>();
        var truncated = false;
        var controls = 0;
        void Walk(JsonElement node, int level)
        {
            if (lines.Count >= MaxLines)
            {
                truncated = true;
                return;
            }

            var role = Clean(node.TryGetProperty("role", out var r) && r.TryGetProperty("value", out var rv) ? rv.ToString() : "node", 40);
            var ignored = node.TryGetProperty("ignored", out var ig) && ig.GetBoolean();
            var name = node.TryGetProperty("name", out var n) && n.TryGetProperty("value", out var nv) ? Squash(nv.ToString()) : string.Empty;
            var next = level;
            // Generic wrappers without a name and the per-line text boxes under every piece of text only cost tokens.
            if (!ignored && role is not ("InlineTextBox" or "LineBreak") && !(role is "generic" or "none" && name.Length == 0))
            {
                var properties = Properties(node);
                var actionable = role != "RootWebArea"
                    && (properties.ContainsKey("focusable") || role is "button" or "link" or "textbox" or "searchbox" or "combobox" or "checkbox" or "radio" or "option" or "menuitem" or "tab" or "switch" or "slider" or "spinbutton");
                var reference = string.Empty;
                if (actionable && node.TryGetProperty("backendDOMNodeId", out var backend))
                {
                    reference = "e" + (++tab.NextRef).ToString(CultureInfo.InvariantCulture);
                    refs[reference] = new ElementRef(backend.GetInt64(), role, name);
                    controls++;
                }

                var flags = new StringBuilder();
                foreach (var flag in (string[])["checked", "disabled", "expanded", "selected", "required", "invalid"])
                {
                    if (properties.TryGetValue(flag, out var value) && value is not ("false" or ""))
                        flags.Append(' ').Append(flag).Append('=').Append(value);
                }

                if (role is "textbox" or "searchbox" or "combobox" && node.TryGetProperty("value", out var current) && current.TryGetProperty("value", out var text))
                    flags.Append(" value=").Append(Quote(Clean(text.ToString(), 200)));

                lines.Add($"{new string(' ', level * 2)}{(reference.Length > 0 ? "@" + reference + " " : string.Empty)}[{role}] {Quote(Clean(name, 300))}{flags}");
                next = level + 1;
                // A text field's children are its own text: the value above says it.
                if (role is "textbox" or "searchbox")
                    return;
            }

            if (node.TryGetProperty("childIds", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (child.GetString() is { } id && nodes.TryGetValue(id, out var childNode))
                        Walk(childNode, next);
                }
            }
        }

        Walk(root, 0);
        tab.ReplaceRefs(refs);
        var shown = find is null ? lines : [.. lines.Where(line => line.Contains(find, StringComparison.OrdinalIgnoreCase))];
        var content = string.Join('\n', shown);
        note.Facts = find is null
            ? $"{controls} control{(controls == 1 ? "" : "s")}"
            : $"{shown.Count} match{(shown.Count == 1 ? "" : "es")}";
        return new AgentBrowserResult
        {
            Tab = await InfoAsync(page, ct),
            Content = content.Length <= MaxText ? content : content[..MaxText],
            Truncated = truncated || content.Length > MaxText,
        };
    }

    private static Dictionary<string, string> Properties(JsonElement node)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.TryGetProperty("properties", out var list))
        {
            foreach (var property in list.EnumerateArray())
            {
                if (property.TryGetProperty("name", out var name) && property.TryGetProperty("value", out var value) && value.TryGetProperty("value", out var inner))
                    properties[name.GetString() ?? string.Empty] = inner.ValueKind is JsonValueKind.True ? "true" : inner.ValueKind is JsonValueKind.False ? "false" : inner.ToString();
            }
        }

        return properties;
    }

    private static string Quote(string text)
        => "\"" + JsonEncodedText.Encode(text, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"";

    private static string Squash(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clean(string text, int max) => text.Length <= max ? text : text[..max];

    private static async Task<AgentBrowserResult> EvaluateAsync(Page page, AgentBrowserAction action, AgentBrowserLimits limits, CancellationToken ct)
    {
        if (!limits.Settings.Scripts)
        {
            return AgentBrowserResult.Fail(AgentBrowserFailures.Denied,
                "Running scripts in pages is off in Fleet (Settings → Browser → Run scripts in pages). Use snapshot, find, console and network.list instead; don't retry.");
        }

        if (string.IsNullOrWhiteSpace(action.Script))
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "\"script\" is required.");

        JsonDocument reply;
        if (action.Ref is { } reference)
        {
            var objectId = await page.ObjectAsync(page.Tab.Element(reference), ct);
            reply = await page.SendAsync("Runtime.callFunctionOn", w =>
            {
                w.WriteString("objectId", objectId);
                w.WriteString("functionDeclaration", action.Script);
                w.WriteStartArray("arguments");
                w.WriteStartObject();
                w.WriteString("objectId", objectId);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
                w.WriteBoolean("userGesture", true);
            }, ct);
        }
        else
        {
            reply = await page.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", action.Script);
                w.WriteBoolean("awaitPromise", true);
                w.WriteBoolean("returnByValue", true);
                w.WriteBoolean("userGesture", true);
            }, ct);
        }

        using (reply)
        {
            var result = reply.RootElement.GetProperty("result");
            if (result.TryGetProperty("exceptionDetails", out var thrown))
            {
                var detail = thrown.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                    ? description.GetString()
                    : thrown.TryGetProperty("text", out var text) ? text.GetString() : "an exception";
                return AgentBrowserResult.Fail(AgentBrowserFailures.Failed,
                    $"The page's JavaScript threw: {Clean(detail ?? string.Empty, 800)}. Check the script before running anything with side effects again.");
            }

            JsonElement? value = result.GetProperty("result").TryGetProperty("value", out var v) ? v.Clone() : null;
            if (value is { } json && json.GetRawText().Length > MaxResultJson)
                return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, $"The result is over {MaxResultJson} characters of JSON. Return only the fields you need.");
            return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Value = value };
        }
    }

    private async Task<AgentBrowserResult> ScreenshotAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        if (action.Ref is not null && action.FullPage == true)
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "Choose either a ref for one element or fullPage for the whole page, not both.");

        var format = action.Format is "jpeg" or "webp" ? action.Format : "png";
        using var metrics = await page.SendAsync("Page.getLayoutMetrics", null, ct);
        var root = metrics.RootElement.GetProperty("result");
        var viewport = root.GetProperty("cssVisualViewport");
        double x = viewport.GetProperty("pageX").GetDouble(), y = viewport.GetProperty("pageY").GetDouble();
        double width = viewport.GetProperty("clientWidth").GetDouble(), height = viewport.GetProperty("clientHeight").GetDouble();
        if (action.Ref is { } reference)
        {
            var box = await page.BoxAsync(page.Tab.Element(reference), ct);
            (x, y, width, height) = (box.X + x, box.Y + y, box.Width, box.Height);
            note.Box = box;
        }
        else if (action.FullPage == true)
        {
            var content = root.GetProperty("cssContentSize");
            (x, y, width, height) = (0, 0, content.GetProperty("width").GetDouble(), content.GetProperty("height").GetDouble());
        }

        if (width <= 0 || height <= 0)
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "That element has no visible area. Read the page again and pick a visible element, or leave out ref.");
        var scale = Math.Min(1, (action.MaxWidth ?? 2000) / width);
        if (width * height * scale * scale > 16_000_000)
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "That screenshot would be over 16 megapixels; shoot one element or pass a smaller maxWidth.");

        using var shot = await page.SendAsync("Page.captureScreenshot", w =>
        {
            w.WriteString("format", format);
            if (format != "png")
                w.WriteNumber("quality", Math.Clamp(action.Quality ?? 80, 1, 100));
            w.WriteBoolean("captureBeyondViewport", true);
            w.WriteStartObject("clip");
            w.WriteNumber("x", x);
            w.WriteNumber("y", y);
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
            w.WriteNumber("scale", scale);
            w.WriteEndObject();
        }, ct);
        var bytes = Convert.FromBase64String(shot.RootElement.GetProperty("result").GetProperty("data").GetString()!);

        if (format == "png" && _screenshots is not null)
        {
            var kept = await _screenshots.SaveAsync(page.Tab.FleetSession, bytes, ct);
            if (kept is not null)
                note.Screenshot = new ScreenshotReference(page.Tab.FleetSession, kept, (int)Math.Round(width * scale), (int)Math.Round(height * scale));
        }

        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Image = bytes, ImageMime = "image/" + format };
    }

    private static async Task<AgentBrowserResult> ConsoleAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var floor = Severity(action.Level ?? "debug");
        var limit = Math.Clamp(action.Limit ?? 100, 1, 500);
        AgentConsoleEntry[] matching;
        int dropped;
        lock (page.Tab)
        {
            matching = [.. page.Tab.Console.Where(entry => Severity(entry.Level) >= floor)];
            dropped = page.Tab.DroppedConsole;
        }

        var shown = matching.Length <= limit ? matching : matching[^limit..];
        var errors = matching.Count(entry => entry.Level == "error");
        note.Facts = $"{matching.Length} line{(matching.Length == 1 ? "" : "s")}, " + (errors == 0 ? "no errors" : $"{errors} error{(errors == 1 ? "" : "s")}");
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Console = shown, Truncated = matching.Length > limit, Dropped = dropped };
    }

    private static int Severity(string level) => level switch { "error" => 3, "warning" => 2, "info" => 1, _ => 0 };

    private static async Task<AgentBrowserResult> RequestsAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var limit = Math.Clamp(action.Limit ?? 100, 1, 500);
        AgentNetworkRequest[] matching;
        int dropped;
        lock (page.Tab)
        {
            matching = [.. page.Tab.Requests.Values
                .Select(request => request.Summary())
                .Where(request => (action.UrlContains is not { Length: > 0 } part || request.Url.Contains(part, StringComparison.Ordinal))
                    && (action.ResourceType is not { Length: > 0 } type || request.ResourceType == type))
                .OrderBy(request => request.TimestampMs)];
            dropped = page.Tab.DroppedRequests;
        }

        var shown = matching.Length <= limit ? matching : matching[^limit..];
        var failed = matching.Count(request => request.State == "failed" || request.StatusCode >= 400);
        note.Facts = $"{matching.Length} request{(matching.Length == 1 ? "" : "s")}, " + (failed == 0 ? "none failed" : $"{failed} failed");
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Requests = shown, Truncated = matching.Length > limit, Dropped = dropped };
    }

    private static async Task<AgentBrowserResult> RequestAsync(Page page, AgentBrowserAction action, CancellationToken ct)
    {
        RequestRecord? record;
        lock (page.Tab)
            record = action.RequestId is { } id && page.Tab.Requests.TryGetValue(id, out var found) ? found : null;
        if (record is null)
        {
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed,
                "No such request on this tab's current page. Request ids expire when the page changes: call network.list again and use an id it returns.");
        }

        var responseBody = new AgentBody("notRequested");
        var requestBody = record.PostData is null ? new AgentBody("empty") : Body(record.PostData, action.MaxBodyChars);
        if (action.IncludeBody == true)
        {
            if (record.State == "pending")
            {
                responseBody = new AgentBody("pending");
            }
            else
            {
                try
                {
                    using var body = await page.SendAsync("Network.getResponseBody", w => w.WriteString("requestId", record.Id), ct);
                    var result = body.RootElement.GetProperty("result");
                    responseBody = result.GetProperty("base64Encoded").GetBoolean()
                        ? new AgentBody("unavailable", Reason: "binary")
                        : Body(result.GetProperty("body").GetString() ?? string.Empty, action.MaxBodyChars);
                }
                catch (CdpException)
                {
                    responseBody = new AgentBody("unavailable", Reason: "notCaptured");
                }
            }
        }

        AgentNetworkDetail detail;
        lock (page.Tab)
            detail = new AgentNetworkDetail(record.Summary(), [.. record.RequestHeaders], [.. record.ResponseHeaders], requestBody, responseBody);
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Request = detail };
    }

    private static AgentBody Body(string text, int? maxChars)
    {
        if (text.Length == 0)
            return new AgentBody("empty");
        var max = Math.Clamp(maxChars ?? 4000, 1, 20_000);
        return text.Length <= max ? new AgentBody("text", text) : new AgentBody("text", text[..max], Truncated: true);
    }

    private static async Task<AgentBrowserResult> DialogAsync(Page page, AgentBrowserAction action, CancellationToken ct)
    {
        var open = page.Tab.Dialog;
        if (action.DialogAction is "accept" or "dismiss")
        {
            if (open is null)
                return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, "There's no dialog open on this tab.");
            (await page.SendAsync("Page.handleJavaScriptDialog", w =>
            {
                w.WriteBoolean("accept", action.DialogAction == "accept");
                if (action.PromptText is not null)
                    w.WriteString("promptText", action.PromptText);
            }, ct)).Dispose();
            page.Tab.Dialog = null;
            return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Dialog = open };
        }

        return new AgentBrowserResult { Tab = await InfoAsync(page, ct), Dialog = open };
    }

    private static async Task<AgentBrowserResult> WaitAsync(Page page, AgentBrowserAction action, CancellationToken ct)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(action.TimeoutMs ?? 10_000, 1, 30_000));
        var condition = action.Condition ?? "load";
        if (condition is "text" or "textGone" && string.IsNullOrEmpty(action.Text))
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, $"\"text\" is required to wait for {condition}.");

        var probe = condition switch
        {
            "text" => $"document.body !== null && document.body.innerText.includes({Quote(action.Text!)})",
            "textGone" => $"document.body === null || !document.body.innerText.includes({Quote(action.Text!)})",
            _ => "document.readyState === 'complete'",
        };
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            using (var reply = await page.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", probe);
                w.WriteBoolean("returnByValue", true);
            }, ct))
            {
                if (reply.RootElement.GetProperty("result").GetProperty("result").TryGetProperty("value", out var met) && met.ValueKind == JsonValueKind.True)
                    return new AgentBrowserResult { Tab = await InfoAsync(page, ct) };
            }

            await Task.Delay(100, ct);
        }

        return AgentBrowserResult.Fail(AgentBrowserFailures.Failed, condition switch
        {
            "text" => $"\"{action.Text}\" didn't appear within {timeout.TotalMilliseconds:0} ms. Read the page to see what it shows instead.",
            "textGone" => $"\"{action.Text}\" was still there after {timeout.TotalMilliseconds:0} ms.",
            _ => $"The page hadn't finished loading after {timeout.TotalMilliseconds:0} ms.",
        }) with { Tab = await InfoAsync(page, ct) };
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<AgentBrowserResult> InputAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var tab = page.Tab;
        var act = action.Kind switch
        {
            AgentBrowserKinds.Click => ClickAsync(page, action, note, ct),
            AgentBrowserKinds.Hover => HoverAsync(page, action, note, ct),
            AgentBrowserKinds.Drag => DragAsync(page, action, note, ct),
            AgentBrowserKinds.Fill => FillAsync(page, Require(action.Ref, "ref"), action.Text ?? string.Empty, note, ct),
            AgentBrowserKinds.FillForm => FillFormAsync(page, action, note, ct),
            AgentBrowserKinds.Select => SelectAsync(page, Require(action.Ref, "ref"), action.Values ?? [], note, ct),
            AgentBrowserKinds.Check => CheckAsync(page, Require(action.Ref, "ref"), action.Checked ?? true, note, ct),
            AgentBrowserKinds.Press => PressAsync(page, Require(action.Key, "key"), ct),
            AgentBrowserKinds.Scroll => ScrollAsync(page, action.DeltaX ?? 0, action.DeltaY ?? 0, ct),
            _ => null,
        };
        if (act is null)
            return AgentBrowserResult.Fail(AgentBrowserFailures.Unsupported, $"Fleet's browser doesn't know {action.Kind}.");

        // A validation alert, say, opens a dialog that blocks the page: the action can't finish until it's answered.
        tab.DialogOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = await Task.WhenAny(act, tab.DialogOpened.Task);
        if (finished != act)
        {
            _ = act.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return AgentBrowserResult.Fail(AgentBrowserFailures.Failed,
                $"A {tab.Dialog?.Type ?? "JavaScript"} dialog opened while the action ran (\"{tab.Dialog?.Message}\"). Read it with dialog get and accept or dismiss it; don't repeat the action just to close it.")
                with { Tab = tab.Snapshot() };
        }

        await act;
        // Let what the action started (a click's navigation, a re-render) begin before the tab is read.
        await Task.Delay(120, ct);
        return new AgentBrowserResult { Tab = await InfoAsync(page, ct) };
    }

    private static string Require(string? value, string name)
        => string.IsNullOrEmpty(value) ? throw new ElementException($"\"{name}\" is required.") : value;

    private static async Task ClickAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var element = page.Tab.Element(Require(action.Ref, "ref"));
        note.Target = element.Label;
        var box = await page.BoxAsync(element, ct, scroll: true);
        note.Box = box;
        var (x, y) = box.Center();
        var button = action.Button is "right" or "middle" ? action.Button : "left";
        var count = action.Count == 2 ? 2 : 1;
        var modifiers = Modifiers(action.Modifiers);
        await page.MouseAsync("mouseMoved", x, y, "none", 0, modifiers, ct);
        for (var click = 1; click <= count; click++)
        {
            await page.MouseAsync("mousePressed", x, y, button, click, modifiers, ct);
            await page.MouseAsync("mouseReleased", x, y, button, click, modifiers, ct);
        }
    }

    private static async Task HoverAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var element = page.Tab.Element(Require(action.Ref, "ref"));
        note.Target = element.Label;
        var box = await page.BoxAsync(element, ct, scroll: true);
        note.Box = box;
        var (x, y) = box.Center();
        await page.MouseAsync("mouseMoved", x, y, "none", 0, 0, ct);
    }

    private static async Task DragAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        var from = page.Tab.Element(Require(action.FromRef, "from"));
        var to = page.Tab.Element(Require(action.ToRef, "to"));
        note.Target = from.Label;
        var start = await page.BoxAsync(from, ct, scroll: true);
        note.Box = start;
        var (x1, y1) = start.Center();
        var (x2, y2) = (await page.BoxAsync(to, ct)).Center();
        await page.MouseAsync("mouseMoved", x1, y1, "none", 0, 0, ct);
        await page.MouseAsync("mousePressed", x1, y1, "left", 1, 0, ct);
        for (var i = 1; i <= 8; i++)
            await page.MouseAsync("mouseMoved", x1 + ((x2 - x1) * i / 8), y1 + ((y2 - y1) * i / 8), "left", 0, 0, ct);
        await page.MouseAsync("mouseReleased", x2, y2, "left", 1, 0, ct);
    }

    private const string FocusAndSelect =
        "function(){this.scrollIntoView({block:'center',inline:'center'});this.focus();"
        + "if(this.isContentEditable){const r=document.createRange();r.selectNodeContents(this);const s=getSelection();s.removeAllRanges();s.addRange(r);return 'editable'}"
        + "if('value' in this&&this.tagName!=='SELECT'&&this.type!=='checkbox'&&this.type!=='radio'){try{this.select()}catch(e){}return 'input'}return 'other'}";

    private static async Task FillAsync(Page page, string reference, string text, StepNote note, CancellationToken ct)
    {
        var element = page.Tab.Element(reference);
        note.Target ??= element.Label;
        note.Box ??= await page.BoxAsync(element, ct, scroll: true);
        var kind = await page.CallAsync(element, FocusAndSelect, null, ct);
        if (kind.ValueKind != JsonValueKind.String || kind.GetString() == "other")
            throw new ElementException($"{element.Label} doesn't take text. Use a text box's ref; use select for dropdowns and check for checkboxes.");

        if (text.Length == 0)
        {
            await page.KeyAsync("keyDown", "Backspace", "Backspace", 8, null, 0, ct);
            await page.KeyAsync("keyUp", "Backspace", "Backspace", 8, null, 0, ct);
        }
        else
        {
            (await page.SendAsync("Input.insertText", w => w.WriteString("text", text), ct)).Dispose();
        }
    }

    private static async Task FillFormAsync(Page page, AgentBrowserAction action, StepNote note, CancellationToken ct)
    {
        foreach (var field in action.Fields ?? [])
        {
            switch (field.Type)
            {
                case "text":
                    await FillAsync(page, field.Ref, field.Value ?? string.Empty, note, ct);
                    break;
                case "select":
                    await SelectAsync(page, field.Ref, field.Values ?? [], note, ct);
                    break;
                case "check":
                    await CheckAsync(page, field.Ref, field.Checked ?? true, note, ct);
                    break;
                default:
                    throw new ElementException($"A field's type is \"text\", \"select\" or \"check\", not \"{field.Type}\".");
            }
        }

        note.Target = null;
    }

    private const string SelectValues =
        "function(values){if(this.tagName!=='SELECT')return {error:'not a dropdown'};"
        + "const options=[...this.options];const missing=values.filter(v=>!options.some(o=>o.value===v));"
        + "if(missing.length)return {error:'no option '+JSON.stringify(missing)+'; options are '+JSON.stringify(options.map(o=>o.value))};"
        + "if(!this.multiple&&values.length>1)return {error:'it takes one value'};"
        + "for(const o of options)o.selected=values.includes(o.value);"
        + "this.dispatchEvent(new Event('input',{bubbles:true}));this.dispatchEvent(new Event('change',{bubbles:true}));return {ok:true}}";

    private static async Task SelectAsync(Page page, string reference, IReadOnlyList<string> values, StepNote note, CancellationToken ct)
    {
        var element = page.Tab.Element(reference);
        note.Target ??= element.Label;
        note.Box ??= await page.BoxAsync(element, ct, scroll: true);
        var outcome = await page.CallAsync(element, SelectValues, w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("value");
            foreach (var value in values)
                w.WriteStringValue(value);
            w.WriteEndArray();
            w.WriteEndObject();
        }, ct);
        if (outcome.ValueKind == JsonValueKind.Object && outcome.TryGetProperty("error", out var error))
            throw new ElementException($"Couldn't pick that in {element.Label}: {error.GetString()}. Use the option values, not their labels.");
    }

    private static async Task CheckAsync(Page page, string reference, bool wanted, StepNote note, CancellationToken ct)
    {
        var element = page.Tab.Element(reference);
        note.Target ??= element.Label;
        var state = await page.CallAsync(element, "function(){return typeof this.checked==='boolean'?this.checked:(this.getAttribute('aria-checked')==='true')}", null, ct);
        if (state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ElementException($"{element.Label} isn't a checkbox or radio button.");
        if (state.GetBoolean() == wanted)
            return;
        var box = await page.BoxAsync(element, ct, scroll: true);
        note.Box ??= box;
        var (x, y) = box.Center();
        await page.MouseAsync("mousePressed", x, y, "left", 1, 0, ct);
        await page.MouseAsync("mouseReleased", x, y, "left", 1, 0, ct);
    }

    private static async Task PressAsync(Page page, string chord, CancellationToken ct)
    {
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new ElementException("\"key\" is empty. Name a key such as Enter, Tab, ArrowDown or Control+A.");
        var key = parts[^1];
        var modifiers = Modifiers(parts[..^1]);
        var (name, code, keyCode, text) = Keys.Describe(key)
            ?? throw new ElementException($"\"{key}\" isn't a key Fleet knows. Use names such as Enter, Tab, Escape, Backspace, ArrowDown, PageDown, a letter or a digit.");
        if ((modifiers & ~8) != 0)
            text = null; // Control+A selects; it doesn't type an "a".
        await page.KeyAsync(text is null ? "rawKeyDown" : "keyDown", name, code, keyCode, text, modifiers, ct);
        await page.KeyAsync("keyUp", name, code, keyCode, null, modifiers, ct);
    }

    private static async Task ScrollAsync(Page page, int deltaX, int deltaY, CancellationToken ct)
        => (await page.SendAsync("Input.dispatchMouseEvent", w =>
        {
            w.WriteString("type", "mouseWheel");
            w.WriteNumber("x", Width / 2);
            w.WriteNumber("y", Height / 2);
            w.WriteNumber("deltaX", Math.Clamp(deltaX, -10_000, 10_000));
            w.WriteNumber("deltaY", Math.Clamp(deltaY, -10_000, 10_000));
        }, ct)).Dispose();

    private static int Modifiers(IEnumerable<string>? names)
    {
        var bits = 0;
        foreach (var name in names ?? [])
        {
            bits |= name.ToLowerInvariant() switch
            {
                "alt" => 1,
                "control" or "ctrl" => 2,
                "meta" or "cmd" or "command" => 4,
                "shift" => 8,
                _ => throw new ElementException($"\"{name}\" isn't a modifier; use Alt, Control, Meta or Shift."),
            };
        }

        return bits;
    }

    // ── Agent's view ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<byte[]?> FrameAsync(string sessionId, string tabId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || !TryTab(session, tabId, out var tab) || _cdp is not { IsOpen: true } cdp)
            return null;
        session.LastWatched = Clock();
        try
        {
            await FrontAsync(new Page(cdp, tab), ct);
        }
        catch (CdpException)
        {
            return tab.Frame;
        }
        if (tab.Frame is { } cached && Clock() - tab.FrameAt < FrameCache)
            return cached;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var shot = await cdp.SendAsync("Page.captureScreenshot", w =>
            {
                w.WriteString("format", "jpeg");
                w.WriteNumber("quality", 60);
            }, tab.CdpSession, timeout.Token);
            var frame = Convert.FromBase64String(shot.RootElement.GetProperty("result").GetProperty("data").GetString()!);
            (tab.Frame, tab.FrameAt) = (frame, Clock());
            return frame;
        }
        catch (Exception error) when (error is CdpException or OperationCanceledException)
        {
            return tab.Frame;
        }
    }

    // ── Steps ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RecordAsync(AgentBrowserCall call, AgentBrowserAction action, AgentBrowserResult result, StepNote note, TimeSpan took, CancellationToken ct)
    {
        if (_steps is null)
            return;
        var tab = result.Tab;
        var step = new AgentBrowserStep
        {
            SessionId = call.SessionId,
            At = Clock(),
            Kind = action.Kind,
            Summary = result.Ok ? AgentBrowserStepText.Summary(action, note.Target, note.Facts, tab) : AgentBrowserStepText.Failed(action, note.Target),
            Detail = AgentBrowserStepText.Detail(action, took),
            Ok = result.Ok,
            Error = result.Failure?.Message,
            CallId = call.CallId ?? _calls?.Current(call.SessionId),
            TabId = tab?.Id ?? action.TabId,
            Url = tab?.Url,
            Title = tab?.Title,
            Box = note.Box,
            Screenshot = note.Screenshot,
        };
        try
        {
            await _steps.RecordAsync(step, call.UserId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogStepFailed(_logger, call.SessionId, error);
        }
    }

    // ── Page events (on the CDP read loop: short, no throwing) ───────────────────────────────────────────

    private static void OnPageEvent(CdpConnection cdp, Tab tab, string method, JsonElement p)
    {
        switch (method)
        {
            case "Fetch.requestPaused":
                Police(cdp, tab, p);
                break;
            case "Page.frameNavigated" when p.TryGetProperty("frame", out var frame) && !frame.TryGetProperty("parentId", out _):
                lock (tab)
                {
                    tab.Url = frame.GetProperty("url").GetString() ?? tab.Url;
                    tab.Generation++;
                    tab.Refs.Clear();
                    // The page Fleet put up for a blocked address keeps saying why; any other page clears it.
                    if (tab.Url != tab.BlockedUrl)
                        (tab.LoadError, tab.BlockedUrl) = (null, null);
                }

                break;
            case "Page.loadEventFired":
                tab.Loading = false;
                break;
            case "Page.frameStartedLoading" when p.TryGetProperty("frameId", out var started) && started.GetString() == tab.TargetId:
                tab.Loading = true;
                break;
            case "Page.javascriptDialogOpening":
                tab.Dialog = new AgentDialog(
                    p.GetProperty("type").GetString() ?? "alert",
                    Clean(p.GetProperty("message").GetString() ?? string.Empty, 2000),
                    p.TryGetProperty("defaultPrompt", out var prompt) ? prompt.GetString() ?? string.Empty : string.Empty);
                tab.DialogOpened?.TrySetResult();
                break;
            case "Page.javascriptDialogClosed":
                tab.Dialog = null;
                break;
            case "Runtime.consoleAPICalled":
                AddConsole(tab, ConsoleLevel(p.GetProperty("type").GetString()), ConsoleText(p), Source(p));
                break;
            case "Runtime.exceptionThrown" when p.TryGetProperty("exceptionDetails", out var details):
                var description = details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var d)
                    ? d.GetString()
                    : details.TryGetProperty("text", out var t) ? t.GetString() : null;
                AddConsole(tab, "error", "Uncaught " + (description ?? "exception"),
                    details.TryGetProperty("url", out var url) ? (url.GetString(), details.TryGetProperty("lineNumber", out var line) ? line.GetInt32() : null, details.TryGetProperty("columnNumber", out var column) ? column.GetInt32() : null) : null);
                break;
            case "Log.entryAdded" when p.TryGetProperty("entry", out var entry):
                var level = entry.GetProperty("level").GetString() switch { "error" => "error", "warning" => "warning", "verbose" => "debug", _ => "info" };
                AddConsole(tab, level, entry.GetProperty("text").GetString() ?? string.Empty,
                    entry.TryGetProperty("url", out var source) ? (source.GetString(), entry.TryGetProperty("lineNumber", out var l) ? l.GetInt32() : null, null) : null);
                break;
            case "Network.requestWillBeSent":
                OnRequest(tab, p);
                break;
            case "Network.responseReceived" when p.TryGetProperty("response", out var response):
                lock (tab)
                {
                    if (tab.Requests.TryGetValue(p.GetProperty("requestId").GetString()!, out var record))
                    {
                        record.Status = response.GetProperty("status").GetInt32();
                        record.ResponseHeaders = Headers(response);
                    }
                }

                break;
            case "Network.loadingFinished":
                Settle(tab, p, "completed", null);
                break;
            case "Network.loadingFailed":
                Settle(tab, p, "failed", (p.TryGetProperty("canceled", out var canceled) && canceled.GetBoolean() ? "canceled: " : string.Empty) + (p.GetProperty("errorText").GetString() ?? "failed"));
                break;
        }
    }

    /// <summary>Lets the main frame load only pages the user's settings allow; everything else continues.</summary>
    /// <summary>
    /// Lets the main frame load only pages the user's settings allow; everything else continues. A blocked page is
    /// answered with a short page of Fleet's saying why, so the tab (and Agent's view) shows that rather than an error.
    /// </summary>
    private static void Police(CdpConnection cdp, Tab tab, JsonElement p)
    {
        var requestId = p.GetProperty("requestId").GetString()!;
        var frameId = p.TryGetProperty("frameId", out var frame) ? frame.GetString() : null;
        var url = p.GetProperty("request").GetProperty("url").GetString() ?? string.Empty;
        string? refusal = null;
        if (frameId == tab.TargetId && Uri.TryCreate(url, UriKind.Absolute, out var target) && tab.Limits is { } limits && !limits.Allows(target))
        {
            refusal = limits.Refusal(target);
            lock (tab)
            {
                tab.BlockedUrl = url;
                tab.LoadError = $"Fleet blocked {url}: {refusal}";
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                (await cdp.SendAsync(refusal is null ? "Fetch.continueRequest" : "Fetch.fulfillRequest", w =>
                {
                    w.WriteString("requestId", requestId);
                    if (refusal is null)
                        return;
                    w.WriteNumber("responseCode", 403);
                    w.WriteStartArray("responseHeaders");
                    w.WriteStartObject();
                    w.WriteString("name", "Content-Type");
                    w.WriteString("value", "text/html; charset=utf-8");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteBase64String("body", Encoding.UTF8.GetBytes(BlockedPage(url, refusal)));
                }, tab.CdpSession)).Dispose();
            }
            catch (CdpException)
            {
                // The tab or the browser went away.
            }
        });
    }

    private static string BlockedPage(string url, string refusal)
        => "<!doctype html><meta charset=utf-8><link rel=icon href=\"data:,\"><title>Blocked by Fleet</title>"
           + "<body style=\"font:15px system-ui,sans-serif;margin:40px;max-width:640px;color:#1a1918\">"
           + "<h1 style=\"font-size:20px\">Fleet blocked this page</h1><p>" + WebUtility.HtmlEncode(url) + "</p><p>"
           + WebUtility.HtmlEncode(refusal) + "</p></body>";

    private static void OnRequest(Tab tab, JsonElement p)
    {
        var id = p.GetProperty("requestId").GetString()!;
        var request = p.GetProperty("request");
        var type = p.TryGetProperty("type", out var t) ? ResourceType(t.GetString()) : "other";
        lock (tab)
        {
            // A new page in the main frame: the requests and console are about the page now showing.
            if (type == "document" && p.TryGetProperty("frameId", out var frame) && frame.GetString() == tab.TargetId
                && p.TryGetProperty("loaderId", out var loader) && loader.GetString() == id)
            {
                tab.Requests.Clear();
                tab.Console.Clear();
                tab.DroppedConsole = tab.DroppedRequests = 0;
            }

            if (tab.Requests.TryGetValue(id, out var redirected))
            {
                // A redirect reuses the id: the earlier hop is done.
                redirected.State = "completed";
                tab.Requests.Remove(id);
            }

            if (tab.Requests.Count >= MaxRequests)
            {
                tab.Requests.Remove(tab.Requests.Values.OrderBy(r => r.TimestampMs).First().Id);
                tab.DroppedRequests++;
            }

            tab.Requests[id] = new RequestRecord(
                id,
                Clean(request.GetProperty("url").GetString() ?? string.Empty, MaxText),
                request.GetProperty("method").GetString() ?? "GET",
                type,
                p.TryGetProperty("wallTime", out var wall) ? wall.GetDouble() * 1000 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                p.TryGetProperty("timestamp", out var mono) ? mono.GetDouble() : 0)
            {
                PostData = request.TryGetProperty("postData", out var post) ? post.GetString() : null,
                RequestHeaders = Headers(request),
            };
        }
    }

    private static void Settle(Tab tab, JsonElement p, string state, string? failure)
    {
        lock (tab)
        {
            if (!tab.Requests.TryGetValue(p.GetProperty("requestId").GetString()!, out var record))
                return;
            record.State = state;
            record.Failure = failure is null ? null : Clean(failure, 2000);
            if (p.TryGetProperty("timestamp", out var mono) && record.Monotonic > 0)
                record.DurationMs = Math.Max(0, (mono.GetDouble() - record.Monotonic) * 1000);
        }
    }

    private static List<AgentHeader> Headers(JsonElement owner)
    {
        List<AgentHeader> headers = [];
        if (owner.TryGetProperty("headers", out var all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in all.EnumerateObject())
                headers.Add(new AgentHeader(header.Name, Clean(header.Value.ToString(), 4000)));
        }

        return headers;
    }

    private static string ResourceType(string? cdp) => cdp switch
    {
        "Document" => "document",
        "Stylesheet" => "stylesheet",
        "Image" => "image",
        "Media" => "media",
        "Font" => "font",
        "Script" => "script",
        "XHR" => "xhr",
        "Fetch" => "fetch",
        "EventSource" => "eventsource",
        "WebSocket" => "websocket",
        "Manifest" => "manifest",
        _ => "other",
    };

    private static string ConsoleLevel(string? type) => type switch
    {
        "error" or "assert" => "error",
        "warning" => "warning",
        "debug" => "debug",
        _ => "info",
    };

    private static string ConsoleText(JsonElement p)
    {
        if (!p.TryGetProperty("args", out var args))
            return string.Empty;
        var parts = new List<string>();
        foreach (var arg in args.EnumerateArray())
        {
            if (arg.TryGetProperty("value", out var value))
                parts.Add(value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText());
            else if (arg.TryGetProperty("unserializableValue", out var raw))
                parts.Add(raw.GetString() ?? string.Empty);
            else if (arg.TryGetProperty("description", out var description))
                parts.Add(description.GetString() ?? string.Empty);
        }

        return string.Join(' ', parts);
    }

    private static (string? Url, int? Line, int? Column)? Source(JsonElement p)
    {
        if (!p.TryGetProperty("stackTrace", out var stack) || !stack.TryGetProperty("callFrames", out var frames) || frames.GetArrayLength() == 0)
            return null;
        var top = frames[0];
        return (top.GetProperty("url").GetString(), top.GetProperty("lineNumber").GetInt32() + 1, top.GetProperty("columnNumber").GetInt32() + 1);
    }

    private static void AddConsole(Tab tab, string level, string text, (string? Url, int? Line, int? Column)? source)
    {
        lock (tab)
        {
            if (tab.Console.Count >= MaxConsole)
            {
                tab.Console.RemoveAt(0);
                tab.DroppedConsole++;
            }

            var truncated = text.Length > 2000;
            tab.Console.Add(new AgentConsoleEntry(
                (++tab.ConsoleSeq).ToString(CultureInfo.InvariantCulture),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                level,
                truncated ? text[..2000] : text,
                truncated,
                source?.Url is { Length: > 0 } url ? url : null,
                source?.Line,
                source?.Column));
        }
    }

    // ── Reading a tab's state ────────────────────────────────────────────────────────────────────────────

    private static async Task<AgentTab> InfoAsync(Page page, CancellationToken ct)
    {
        var tab = page.Tab;
        try
        {
            using var reply = await page.SendAsync("Runtime.evaluate", w =>
            {
                w.WriteString("expression", "JSON.stringify([location.href, document.title, document.readyState])");
                w.WriteBoolean("returnByValue", true);
            }, ct);
            var value = reply.RootElement.GetProperty("result").GetProperty("result");
            if (value.TryGetProperty("value", out var text) && text.GetString() is { } json)
            {
                using var parsed = JsonDocument.Parse(json);
                var fields = parsed.RootElement;
                lock (tab)
                {
                    tab.Url = fields[0].GetString() ?? tab.Url;
                    tab.Title = Clean(fields[1].GetString() ?? string.Empty, 2048);
                }

                tab.Loading = fields[2].GetString() != "complete";
            }

            using var history = await page.SendAsync("Page.getNavigationHistory", null, ct);
            var result = history.RootElement.GetProperty("result");
            var index = result.GetProperty("currentIndex").GetInt32();
            tab.CanGoBack = index > 0;
            tab.CanGoForward = index < result.GetProperty("entries").GetArrayLength() - 1;
        }
        catch (CdpException)
        {
            // A page in the middle of navigating has no context to evaluate in; what's known will do.
        }

        return tab.Snapshot();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _sweep.DisposeAsync();
        foreach (var id in _sessions.Keys)
            await CloseSessionAsync(id);
        _browserEvents?.Dispose();
        _lease?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't record a browser step for session {SessionId}")]
    private static partial void LogStepFailed(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't show the agent's page in a canvas for session {SessionId}")]
    private static partial void LogShowFailed(ILogger logger, string sessionId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing idle browser tabs for session {SessionId} failed")]
    private static partial void LogSweepFailed(ILogger logger, string sessionId, Exception exception);

    // ── State ────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class SessionTabs(string sessionId, string userId)
    {
        public string SessionId { get; } = sessionId;
        public string UserId { get; } = userId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, Tab> Tabs { get; } = new(StringComparer.Ordinal);
        public string? Focused { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public DateTimeOffset LastWatched { get; set; }
    }

    /// <summary>A ref from the latest snapshot: the DOM node, and what the agent saw it as.</summary>
    private sealed record ElementRef(long BackendNodeId, string Role, string Name)
    {
        public string Label => Name.Length > 0 ? "“" + Clean(Name, 60) + "”" : "the " + Role;
    }

    private sealed class Tab(string id, string targetId, string cdpSession, string fleetSession)
    {
        public string Id { get; } = id;
        public string TargetId { get; } = targetId;
        public string CdpSession { get; } = cdpSession;
        public string FleetSession { get; } = fleetSession;
        public IDisposable? Subscription { get; set; }
        public AgentBrowserLimits? Limits { get; set; }
        public bool Gone { get; set; }
        public string Url { get; set; } = "about:blank";
        public string Title { get; set; } = string.Empty;
        public bool Loading { get; set; }
        public string? LoadError { get; set; }
        public string? BlockedUrl { get; set; }
        public bool CanGoBack { get; set; }
        public bool CanGoForward { get; set; }
        public int Generation { get; set; }
        public int NextRef { get; set; }
        public Dictionary<string, ElementRef> Refs { get; } = new(StringComparer.Ordinal);
        public List<AgentConsoleEntry> Console { get; } = [];
        public int ConsoleSeq { get; set; }
        public int DroppedConsole { get; set; }
        public Dictionary<string, RequestRecord> Requests { get; } = new(StringComparer.Ordinal);
        public int DroppedRequests { get; set; }
        public AgentDialog? Dialog { get; set; }
        public TaskCompletionSource? DialogOpened { get; set; }
        public byte[]? Frame { get; set; }
        public DateTimeOffset FrameAt { get; set; }

        public ElementRef Element(string reference)
        {
            var key = reference.TrimStart('@');
            lock (this)
            {
                return Refs.TryGetValue(key, out var element)
                    ? element
                    : throw new ElementException($"There's no ref {reference} on this tab's page. Refs expire when the page changes and with every new snapshot: read the page again and use a ref it returns.");
            }
        }

        public void ReplaceRefs(Dictionary<string, ElementRef> refs)
        {
            lock (this)
            {
                Refs.Clear();
                foreach (var (key, value) in refs)
                    Refs[key] = value;
            }
        }

        public AgentTab Snapshot()
        {
            lock (this)
                return new AgentTab(Id, Url, Title, Loading, LoadError, CanGoBack, CanGoForward, Generation);
        }
    }

    private sealed class RequestRecord(string id, string url, string method, string resourceType, double timestampMs, double monotonic)
    {
        public string Id { get; } = id;
        public double TimestampMs { get; } = timestampMs;
        public double Monotonic { get; } = monotonic;
        public string State { get; set; } = "pending";
        public int? Status { get; set; }
        public double? DurationMs { get; set; }
        public string? Failure { get; set; }
        public string? PostData { get; init; }
        public List<AgentHeader> RequestHeaders { get; init; } = [];
        public List<AgentHeader> ResponseHeaders { get; set; } = [];

        public AgentNetworkRequest Summary() => new(Id, url, method, resourceType, TimestampMs, Status, State, State == "pending" ? null : DurationMs ?? 0, Failure);
    }

    /// <summary>What a step should say beyond the action itself: the element, a count, where it was, a kept screenshot.</summary>
    private sealed class StepNote
    {
        public string? Target { get; set; }
        public string? Facts { get; set; }
        public AgentBox? Box { get; set; }
        public ScreenshotReference? Screenshot { get; set; }
    }

    /// <summary>An element action that can't run as asked, in words the agent can act on.</summary>
    private sealed class ElementException(string message) : Exception(message);

    /// <summary>One tab's CDP session, with the element helpers every action uses.</summary>
    private readonly struct Page(CdpConnection cdp, Tab tab)
    {
        public CdpConnection Cdp { get; } = cdp;
        public Tab Tab { get; } = tab;
        public Task<JsonDocument> SendAsync(string method, Action<Utf8JsonWriter>? parameters, CancellationToken ct)
            => Cdp.SendAsync(method, parameters, Tab.CdpSession, ct);

        public async Task<string> ObjectAsync(ElementRef element, CancellationToken ct)
        {
            try
            {
                using var resolved = await SendAsync("DOM.resolveNode", w => w.WriteNumber("backendNodeId", element.BackendNodeId), ct);
                return resolved.RootElement.GetProperty("result").GetProperty("object").GetProperty("objectId").GetString()!;
            }
            catch (CdpException)
            {
                throw new ElementException($"{element.Label} isn't on the page any more. Read the page again and use a fresh ref.");
            }
        }

        public async Task<JsonElement> CallAsync(ElementRef element, string function, Action<Utf8JsonWriter>? argument, CancellationToken ct)
        {
            var objectId = await ObjectAsync(element, ct);
            using var reply = await SendAsync("Runtime.callFunctionOn", w =>
            {
                w.WriteString("objectId", objectId);
                w.WriteString("functionDeclaration", function);
                if (argument is not null)
                {
                    w.WriteStartArray("arguments");
                    argument(w);
                    w.WriteEndArray();
                }

                w.WriteBoolean("returnByValue", true);
                w.WriteBoolean("userGesture", true);
            }, ct);
            var result = reply.RootElement.GetProperty("result");
            if (result.TryGetProperty("exceptionDetails", out var thrown))
                throw new ElementException($"Acting on {element.Label} failed in the page: {thrown.GetProperty("text").GetString()}");
            return result.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
        }

        /// <summary>The element's box in the viewport, scrolled into view first when asked.</summary>
        public async Task<AgentBox> BoxAsync(ElementRef element, CancellationToken ct, bool scroll = false)
        {
            try
            {
                if (scroll)
                    (await SendAsync("DOM.scrollIntoViewIfNeeded", w => w.WriteNumber("backendNodeId", element.BackendNodeId), ct)).Dispose();
                using var quads = await SendAsync("DOM.getContentQuads", w => w.WriteNumber("backendNodeId", element.BackendNodeId), ct);
                var list = quads.RootElement.GetProperty("result").GetProperty("quads");
                if (list.GetArrayLength() == 0)
                    throw new ElementException($"{element.Label} isn't visible, so it can't be clicked or typed into. Scroll to it or open what hides it, then read the page again.");
                var q = list[0];
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (var i = 0; i < 8; i += 2)
                {
                    minX = Math.Min(minX, q[i].GetDouble());
                    maxX = Math.Max(maxX, q[i].GetDouble());
                    minY = Math.Min(minY, q[i + 1].GetDouble());
                    maxY = Math.Max(maxY, q[i + 1].GetDouble());
                }

                return new AgentBox(Math.Round(minX, 1), Math.Round(minY, 1), Math.Round(maxX - minX, 1), Math.Round(maxY - minY, 1));
            }
            catch (CdpException)
            {
                throw new ElementException($"{element.Label} isn't on the page any more. Read the page again and use a fresh ref.");
            }
        }

        public async Task MouseAsync(string type, double x, double y, string button, int clickCount, int modifiers, CancellationToken ct)
            => (await SendAsync("Input.dispatchMouseEvent", w =>
            {
                w.WriteString("type", type);
                w.WriteNumber("x", x);
                w.WriteNumber("y", y);
                w.WriteString("button", button);
                w.WriteNumber("clickCount", clickCount);
                w.WriteNumber("modifiers", modifiers);
            }, ct)).Dispose();

        public async Task KeyAsync(string type, string key, string code, int keyCode, string? text, int modifiers, CancellationToken ct)
            => (await SendAsync("Input.dispatchKeyEvent", w =>
            {
                w.WriteString("type", type);
                w.WriteString("key", key);
                w.WriteString("code", code);
                w.WriteNumber("windowsVirtualKeyCode", keyCode);
                w.WriteNumber("modifiers", modifiers);
                if (text is not null)
                {
                    w.WriteString("text", text);
                    w.WriteString("unmodifiedText", text);
                }
            }, ct)).Dispose();
    }
}

internal static class Keys
{
    /// <summary>The DevTools key fields for a key name, or null when Fleet doesn't know it.</summary>
    public static (string Key, string Code, int KeyCode, string? Text)? Describe(string name)
    {
        if (name.Length == 1)
        {
            var c = name[0];
            if (char.IsAsciiLetter(c))
                return (name, "Key" + char.ToUpperInvariant(c), char.ToUpperInvariant(c), name);
            if (char.IsAsciiDigit(c))
                return (name, "Digit" + c, c, name);
            return (name, string.Empty, 0, name);
        }

        return name.ToLowerInvariant() switch
        {
            "enter" or "return" => ("Enter", "Enter", 13, "\r"),
            "tab" => ("Tab", "Tab", 9, null),
            "escape" or "esc" => ("Escape", "Escape", 27, null),
            "backspace" => ("Backspace", "Backspace", 8, null),
            "delete" => ("Delete", "Delete", 46, null),
            "space" or "spacebar" => (" ", "Space", 32, " "),
            "arrowup" or "up" => ("ArrowUp", "ArrowUp", 38, null),
            "arrowdown" or "down" => ("ArrowDown", "ArrowDown", 40, null),
            "arrowleft" or "left" => ("ArrowLeft", "ArrowLeft", 37, null),
            "arrowright" or "right" => ("ArrowRight", "ArrowRight", 39, null),
            "home" => ("Home", "Home", 36, null),
            "end" => ("End", "End", 35, null),
            "pageup" => ("PageUp", "PageUp", 33, null),
            "pagedown" => ("PageDown", "PageDown", 34, null),
            "insert" => ("Insert", "Insert", 45, null),
            _ when name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 12
                => ("F" + f, "F" + f, 111 + f, null),
            _ => null,
        };
    }
}

internal static class AgentBoxes
{
    public static (double X, double Y) Center(this AgentBox box) => (box.X + (box.Width / 2), box.Y + (box.Height / 2));
}
