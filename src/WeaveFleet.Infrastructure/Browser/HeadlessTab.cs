namespace WeaveFleet.Infrastructure.Browser;

/// <summary>
/// A tab of its own in Fleet's headless browser, for one look at a page (a screenshot, a page check): opened at a
/// window size, never cached, and closed afterwards so nothing a page does reaches the next one.
/// </summary>
internal sealed class HeadlessTab : IAsyncDisposable
{
    /// <summary>How long closing a tab may take; a browser that doesn't answer is quit anyway.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly CdpConnection _cdp;
    private readonly string _target;

    private HeadlessTab(CdpConnection cdp, string target, string session)
    {
        _cdp = cdp;
        _target = target;
        Session = session;
    }

    /// <summary>The CDP session to send the tab's commands on.</summary>
    public string Session { get; }

    public static async Task<HeadlessTab> OpenAsync(CdpConnection cdp, int width, int height, CancellationToken ct)
    {
        string target;
        using (var created = await cdp.SendAsync("Target.createTarget", write => write.WriteString("url", "about:blank"), ct: ct))
        {
            target = created.RootElement.GetProperty("result").GetProperty("targetId").GetString()!;
        }

        HeadlessTab? tab = null;
        try
        {
            using (var attached = await cdp.SendAsync("Target.attachToTarget", write =>
                   {
                       write.WriteString("targetId", target);
                       write.WriteBoolean("flatten", true);
                   }, ct: ct))
            {
                tab = new HeadlessTab(cdp, target, attached.RootElement.GetProperty("result").GetProperty("sessionId").GetString()!);
            }

            (await cdp.SendAsync("Page.enable", sessionId: tab.Session, ct: ct)).Dispose();
            (await cdp.SendAsync("Network.enable", sessionId: tab.Session, ct: ct)).Dispose();

            // The point of looking is seeing the change just made. A warm browser that serves the page it saw a
            // minute ago would show the old one, and the agent would trust it.
            (await cdp.SendAsync("Network.setCacheDisabled", write => write.WriteBoolean("cacheDisabled", true), tab.Session, ct)).Dispose();

            await tab.ResizeAsync(width, height, ct);
            return tab;
        }
        catch
        {
            if (tab is not null)
                await tab.DisposeAsync();
            else
                await CloseAsync(cdp, target);
            throw;
        }
    }

    /// <summary>
    /// Sizes the window. Not "mobile": that makes Chrome treat a page without a viewport meta tag as 980 CSS px wide
    /// and shrink it to fit, so a narrow window comes back as the desktop layout in miniature. A plain window of the
    /// asked-for size is what a developer dragging their browser narrow sees, and fires the same media queries.
    /// </summary>
    public async Task ResizeAsync(int width, int height, CancellationToken ct)
        => (await _cdp.SendAsync("Emulation.setDeviceMetricsOverride", write =>
        {
            write.WriteNumber("width", width);
            write.WriteNumber("height", height);
            write.WriteNumber("deviceScaleFactor", 1);
            write.WriteBoolean("mobile", false);
        }, Session, ct)).Dispose();

    /// <summary>
    /// Opens <paramref name="url"/> and waits up to <paramref name="timeout"/> for it to load. Returns the browser's
    /// error when it couldn't open the page at all, and whether it finished loading otherwise.
    /// </summary>
    public async Task<(string? Error, bool Loaded)> NavigateAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var loaded = _cdp.Expect("Page.loadEventFired", Session);
        using (var navigated = await _cdp.SendAsync("Page.navigate", write => write.WriteString("url", url), Session, ct))
        {
            var result = navigated.RootElement.GetProperty("result");
            if (result.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } text)
                return (text, false);
        }

        return (null, await loaded.ArrivedAsync(timeout, ct));
    }

    public async ValueTask DisposeAsync() => await CloseAsync(_cdp, _target);

    private static async Task CloseAsync(CdpConnection cdp, string target)
    {
        try
        {
            using var closing = new CancellationTokenSource(CloseTimeout);
            (await cdp.SendAsync("Target.closeTarget", write => write.WriteString("targetId", target), ct: closing.Token)).Dispose();
        }
        catch (Exception error) when (error is CdpException or OperationCanceledException)
        {
            // The tab goes with the browser.
        }
    }
}
