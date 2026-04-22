using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for Fleet sidebar session interactions.
/// </summary>
public sealed class FleetSidebarPage(IPage page)
{
    private readonly IPage _page = page;

    private ILocator SessionLeaves => _page.Locator("[data-tree-leaf]");

    public ILocator GetSessionLeaf(string sessionId)
        => _page.Locator($"[data-tree-leaf][data-session-id='{sessionId}']");

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

    public async Task OpenSessionContextMenuAsync(string sessionId)
        => await GetSessionLeaf(sessionId).ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });

    public async Task ClickSessionMenuItemAsync(string sessionId, string menuItem)
    {
        await OpenSessionContextMenuAsync(sessionId);
        await _page.GetByRole(AriaRole.Menuitem, new() { Name = menuItem }).ClickAsync();
    }

    public Task ExpectSessionVisibleAsync(string sessionId)
        => Assertions.Expect(GetSessionLeaf(sessionId)).ToBeVisibleAsync();

    public Task ExpectSessionHiddenAsync(string sessionId)
        => Assertions.Expect(GetSessionLeaf(sessionId)).ToHaveCountAsync(0);

    // ── Project context menu helpers ─────────────────────────────────────────

    public ILocator GetProjectItem(string projectId)
        => _page.Locator($"[data-project-id='{projectId}']");

    public async Task OpenProjectContextMenuAsync(string projectId)
        => await GetProjectItem(projectId).ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });

    public async Task ClickProjectMenuItemAsync(string projectId, string menuItem)
    {
        await OpenProjectContextMenuAsync(projectId);
        await _page.GetByRole(AriaRole.Menuitem, new() { Name = menuItem }).ClickAsync();
    }
}
