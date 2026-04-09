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
    private ILocator SessionCards => _page.GetByTestId("session-card");

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Navigate to the fleet dashboard and wait for it to settle.</summary>
    public async Task GotoAsync()
    {
        await _page.GotoAsync("/");
        // Wait for either the empty state or a session card to appear
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>Returns all visible session cards.</summary>
    public async Task<IReadOnlyList<ILocator>> GetSessionCardsAsync()
    {
        var cards = SessionCards;
        var count = await cards.CountAsync();
        var result = new List<ILocator>(count);
        for (var i = 0; i < count; i++)
            result.Add(cards.Nth(i));
        return result;
    }

    /// <summary>Returns the session card for a specific session ID.</summary>
    public ILocator GetSessionCard(string sessionId)
        => _page.Locator($"[data-testid='session-card'][data-session-id='{sessionId}']");

    /// <summary>Returns the status indicator for a session card.</summary>
    public ILocator GetSessionStatusIndicator(string sessionId)
        => GetSessionCard(sessionId).GetByTestId("session-status-indicator");

    /// <summary>Returns the title element for a session card.</summary>
    public ILocator GetSessionTitle(string sessionId)
        => GetSessionCard(sessionId).GetByTestId("session-title");

    /// <summary>Get the summary bar locator.</summary>
    public ILocator GetSummaryBar() => SummaryBar;

    /// <summary>Get a specific summary count by label (e.g. "active", "idle").</summary>
    public ILocator GetSummaryCount(string label) => _page.GetByTestId($"summary-{label}-count");

    // ── Waits ─────────────────────────────────────────────────────────────────

    /// <summary>Wait for the empty state to be visible.</summary>
    public Task WaitForEmptyStateAsync()
        => EmptyState.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    /// <summary>Wait for at least N session cards to be visible.</summary>
    public async Task WaitForSessionCountAsync(int minimumCount)
    {
        await Assertions.Expect(SessionCards).ToHaveCountAsync(minimumCount);
    }

    /// <summary>Wait for the summary bar to be visible.</summary>
    public Task WaitForSummaryBarAsync()
        => SummaryBar.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Click "New Session" and return the dialog page object.</summary>
    public async Task<NewSessionDialog> ClickNewSessionAsync()
    {
        await NewSessionButton.ClickAsync();
        var dialog = new NewSessionDialog(_page);
        await dialog.WaitForVisibleAsync();
        return dialog;
    }

    /// <summary>Click the delete button on a session card (trigger delete confirmation).</summary>
    public async Task ClickDeleteSessionAsync(string sessionId)
    {
        var card = GetSessionCard(sessionId);
        await card.HoverAsync();
        await card.GetByTestId("session-delete-button").ClickAsync();
    }

    /// <summary>Click the stop/terminate button on a running session card.</summary>
    public async Task ClickTerminateSessionAsync(string sessionId)
    {
        var card = GetSessionCard(sessionId);
        await card.HoverAsync();
        await card.GetByTestId("session-terminate-button").ClickAsync();
    }

    /// <summary>Click the archive button on a session card.</summary>
    public async Task ClickArchiveSessionAsync(string sessionId)
    {
        var card = GetSessionCard(sessionId);
        await card.HoverAsync();
        await card.GetByTestId("session-archive-button").ClickAsync();
    }

    /// <summary>Click the unarchive button on a session card.</summary>
    public async Task ClickUnarchiveSessionAsync(string sessionId)
    {
        var card = GetSessionCard(sessionId);
        await card.HoverAsync();
        await card.GetByTestId("session-unarchive-button").ClickAsync();
    }

    /// <summary>Assert whether a card contains the archived badge text.</summary>
    public Task ExpectArchivedBadgeAsync(string sessionId)
        => Assertions.Expect(GetSessionCard(sessionId).GetByTestId("session-card-archived-badge")).ToBeVisibleAsync();

    /// <summary>Change the retention filter through the toolbar.</summary>
    public async Task SetRetentionFilterAsync(string label)
    {
        var key = label.ToLowerInvariant();
        var option = _page.GetByTestId($"retention-filter-option-{key}");

        // Radix dropdowns can occasionally miss the initial open click in CI
        // when layout/animation is still settling, so retry the trigger until
        // the target option is actually visible.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await _page.GetByTestId("retention-filter-trigger").ClickAsync(new LocatorClickOptions { Force = true });

            try
            {
                await Assertions.Expect(option).ToBeVisibleAsync(
                    new LocatorAssertionsToBeVisibleOptions { Timeout = 2_000 });
                break;
            }
            catch when (attempt < 2)
            {
                // retry open
            }
        }

        await Assertions.Expect(option).ToBeVisibleAsync();
        await option.ClickAsync(new LocatorClickOptions { Force = true });
    }

    /// <summary>Click a session card to navigate to the session detail page.</summary>
    public async Task<SessionDetailPage> ClickSessionCardAsync(string sessionId)
    {
        await GetSessionCard(sessionId).ClickAsync();
        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
