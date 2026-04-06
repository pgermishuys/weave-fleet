using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the New Session dialog.
/// </summary>
public sealed class NewSessionDialog(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    private ILocator Dialog => _page.GetByTestId("new-session-dialog");
    private ILocator DirectoryInput => _page.GetByLabel("Directory");
    private ILocator TitleInput => _page.GetByLabel("Title");
    private ILocator SubmitButton => _page.GetByTestId("create-session-submit");
    private ILocator DirectoryModeButton => _page.GetByRole(AriaRole.Radio, new PageGetByRoleOptions { Name = "Directory" });

    // ── Waits ─────────────────────────────────────────────────────────────────

    /// <summary>Wait for the dialog to be visible.</summary>
    public Task WaitForVisibleAsync()
        => Dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Fill in the directory field (switches to directory mode first).</summary>
    public async Task SetDirectoryAsync(string directory)
    {
        // Switch to directory mode if not already selected
        if (await DirectoryModeButton.IsVisibleAsync())
            await DirectoryModeButton.ClickAsync();
        await DirectoryInput.FillAsync(directory);
    }

    /// <summary>Fill in the optional title field.</summary>
    public async Task SetTitleAsync(string title)
        => await TitleInput.FillAsync(title);

    /// <summary>Submit the dialog and wait for navigation to the session detail page.</summary>
    public async Task<SessionDetailPage> SubmitAsync()
    {
        await SubmitButton.ClickAsync();

        // Wait for navigation to the session detail page
        await _page.WaitForURLAsync(url => url.Contains("/sessions/"), new PageWaitForURLOptions { Timeout = 5_000 });

        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
