using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the New Session composer, mounted at the <c>/sessions/new</c> route:
/// a message box with Folder, Workspace and "…" chips under it.
/// </summary>
public sealed class NewSessionFormPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    private ILocator Form => _page.GetByTestId("new-session-form");
    private ILocator FolderChip => Form.GetByTestId("new-session-folder-chip");
    private ILocator MoreChip => Form.GetByTestId("new-session-more-chip");
    private ILocator SubmitButton => _page.GetByTestId("create-session-submit");
    private ILocator StartWithoutMessageButton => _page.GetByTestId("create-session-without-message");

    // The chips' menus are portaled to <body>, so these are looked up on the page, not the form.
    private ILocator BrowseFolderOption => _page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Browse for a folder" });
    private ILocator DirectoryInput => _page.Locator("#new-session-directory");
    private ILocator TitleInput => _page.Locator("#session-title");

    // ── Waits ────────────────────────────────────────────────────────────────

    /// <summary>Wait for the composer to be visible.</summary>
    public Task WaitForVisibleAsync()
        => Form.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>Run the session in <paramref name="directory"/>: Folder chip → Browse for a folder → type the path.</summary>
    public async Task SetDirectoryAsync(string directory)
    {
        await FolderChip.ClickAsync();
        await BrowseFolderOption.ClickAsync();
        await DirectoryInput.FillAsync(directory);
        await DirectoryInput.PressAsync("Enter");
        await DirectoryInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    /// <summary>Set the optional title, from the "…" chip.</summary>
    public async Task SetTitleAsync(string title)
    {
        if (!await TitleInput.IsVisibleAsync())
            await MoreChip.ClickAsync();
        await TitleInput.FillAsync(title);
        await TitleInput.PressAsync("Enter");
        await TitleInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
    }

    /// <summary>Create the session and wait for navigation to the session detail page.</summary>
    public Task<SessionDetailPage> SubmitAsync()
        => SubmitAsync(5_000);

    /// <summary>
    /// Create the session and wait for navigation to the session detail page. Sends the typed
    /// message if there is one; otherwise uses "Start without a message".
    /// </summary>
    public async Task<SessionDetailPage> SubmitAsync(int timeoutMs)
    {
        if (await SubmitButton.IsEnabledAsync())
            await SubmitButton.ClickAsync();
        else
            await StartWithoutMessageButton.ClickAsync();

        // Wait for navigation to the session detail page.
        await _page.WaitForURLAsync(url => url.Contains("/sessions/") && !url.Contains("/sessions/new"), new PageWaitForURLOptions { Timeout = timeoutMs });

        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
