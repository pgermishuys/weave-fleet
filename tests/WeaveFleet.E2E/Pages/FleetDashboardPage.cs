using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the Fleet Dashboard (root "/" page).
/// Uses data-testid selectors exclusively to avoid coupling to CSS class names.
/// </summary>
public sealed class FleetDashboardPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    private ILocator SummaryBar => _page.GetByTestId("summary-bar");
    private ILocator NewSessionButton => _page.GetByTestId("new-session-button");
    private ILocator EmptyState => _page.GetByTestId("empty-state");

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Navigate to the fleet dashboard and wait for it to settle.</summary>
    public async Task GotoAsync()
    {
        await _page.GotoAsync("/");
        await WaitForLoadedAsync();
    }

    /// <summary>Wait for the dashboard shell to become interactive.</summary>
    public async Task WaitForLoadedAsync()
    {
        await Assertions.Expect(NewSessionButton).ToBeVisibleAsync();
        await Assertions.Expect(SummaryBar).ToBeVisibleAsync();
    }

    // ── Waits ─────────────────────────────────────────────────────────────────

    /// <summary>Wait for the empty state to be visible.</summary>
    public Task WaitForEmptyStateAsync()
        => EmptyState.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Click "New Session" and return the inline new-session form page object.</summary>
    public async Task<NewSessionFormPage> ClickNewSessionAsync()
    {
        await NewSessionButton.ClickAsync();
        await _page.WaitForURLAsync(new Regex("/sessions/new"));
        var form = new NewSessionFormPage(_page);
        await form.WaitForVisibleAsync();
        return form;
    }
}
