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
    private ILocator DirectoryInput => Dialog.GetByRole(AriaRole.Textbox, new LocatorGetByRoleOptions { Name = "Directory" });
    private ILocator TitleInput => Dialog.Locator("#session-title");
    private ILocator SubmitButton => _page.GetByTestId("create-session-submit");
    private ILocator SourceGroup => _page.GetByLabel("Source");
    private ILocator DirectoryModeButton => SourceGroup.GetByRole(AriaRole.Radio, new LocatorGetByRoleOptions { Name = "Directory" });
    private ILocator RepositoryModeButton => SourceGroup.GetByRole(AriaRole.Radio, new LocatorGetByRoleOptions { Name = "Repository" });

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

    public async Task SelectRepositorySourceAsync()
    {
        if (await RepositoryModeButton.IsVisibleAsync())
            await RepositoryModeButton.ClickAsync();
    }

    public async Task SelectDirectorySourceAsync()
    {
        if (await DirectoryModeButton.IsVisibleAsync())
            await DirectoryModeButton.ClickAsync();
    }

    /// <summary>Fill in the optional title field.</summary>
    public async Task SetTitleAsync(string title)
        => await TitleInput.FillAsync(title);

    /// <summary>Submit the dialog and wait for navigation to the session detail page.</summary>
    public Task<SessionDetailPage> SubmitAsync()
        => SubmitAsync(5_000);

    /// <summary>Submit the dialog and wait for navigation to the session detail page.</summary>
    public async Task<SessionDetailPage> SubmitAsync(int timeoutMs)
    {
        await SubmitButton.ClickAsync();

        // Wait for navigation to the session detail page
        await _page.WaitForURLAsync(url => url.Contains("/sessions/"), new PageWaitForURLOptions { Timeout = timeoutMs });

        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
