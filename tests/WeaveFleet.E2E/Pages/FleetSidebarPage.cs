using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for Fleet sidebar session interactions.
/// </summary>
public sealed class FleetSidebarPage(IPage page)
{
    private readonly IPage _page = page;

    public ILocator GetSessionLeaf(string sessionId)
        => _page.Locator($"[data-tree-leaf][data-session-id='{sessionId}']");

    /// <summary>Open a session from the sidebar (client-side navigation, no reload).</summary>
    public async Task<SessionDetailPage> ClickSessionAsync(string sessionId)
    {
        await GetSessionLeaf(sessionId).ClickAsync();
        await _page.WaitForURLAsync(
            url => url.Contains($"/sessions/{sessionId}", StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 5_000 });

        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
