using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the Analytics page ("/analytics").
/// </summary>
public sealed class AnalyticsPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    // The analytics page doesn't have data-testid attributes yet; we use role/text selectors.
    private ILocator PageHeading => _page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { NameString = "Analytics" });

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Navigate to the analytics page.</summary>
    public async Task GotoAsync()
    {
        await _page.GotoAsync("/analytics");
        await WaitForLoadedAsync();
    }

    /// <summary>Wait for the analytics page heading to appear.</summary>
    public Task WaitForLoadedAsync()
        => PageHeading.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Check whether the page heading is visible.</summary>
    public Task<bool> IsHeadingVisibleAsync() => PageHeading.IsVisibleAsync();

    /// <summary>Check whether any error message is visible on the page.</summary>
    public async Task<bool> HasErrorAsync()
    {
        var errorLocator = _page.GetByText("Failed to load", new PageGetByTextOptions { Exact = false });
        return await errorLocator.IsVisibleAsync();
    }
}
