using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the inline "New Session" form, mounted at the <c>/sessions/new</c> route.
/// Replaces the old <see cref="NewSessionDialog"/> modal flow (see commit 82af02e:
/// "feat(client): replace new session modal with inline form").
/// </summary>
public sealed class NewSessionFormPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    private ILocator Form => _page.GetByTestId("new-session-form");
    private ILocator DirectoryInput => Form.Locator("#new-session-directory");
    private ILocator TitleInput => Form.Locator("#session-title");
    private ILocator SubmitButton => _page.GetByTestId("create-session-submit");
    private ILocator ErrorRegion => _page.GetByTestId("new-session-error");
    private ILocator DirectoryModeButton => Form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Directory" });
    private ILocator RepositoryModeButton => Form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Repository" });
    private ILocator MoreOptionsTrigger => Form.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "More options" });

    // ── Waits ────────────────────────────────────────────────────────────────

    /// <summary>Wait for the inline form to be visible.</summary>
    public Task WaitForVisibleAsync()
        => Form.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    /// <summary>Wait for the error region to be visible (validation/mutation error).</summary>
    public Task WaitForErrorAsync()
        => ErrorRegion.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Actions ──────────────────────────────────────────────────────────────

    /// <summary>Fill in the directory field (switches to directory mode first).</summary>
    public async Task SetDirectoryAsync(string directory)
    {
        // Switch to directory mode if not already selected.
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

    /// <summary>Fill in the optional title field (opens the "More options" section first).</summary>
    public async Task SetTitleAsync(string title)
    {
        if (!await TitleInput.IsVisibleAsync())
            await MoreOptionsTrigger.ClickAsync();
        await TitleInput.FillAsync(title);
    }

    /// <summary>Submit the form and wait for navigation to the session detail page.</summary>
    public Task<SessionDetailPage> SubmitAsync()
        => SubmitAsync(5_000);

    /// <summary>Submit the form and wait for navigation to the session detail page.</summary>
    public async Task<SessionDetailPage> SubmitAsync(int timeoutMs)
    {
        await SubmitButton.ClickAsync();

        // Wait for navigation to the session detail page.
        await _page.WaitForURLAsync(url => url.Contains("/sessions/") && !url.Contains("/sessions/new"), new PageWaitForURLOptions { Timeout = timeoutMs });

        var detail = new SessionDetailPage(_page);
        await detail.WaitForLoadedAsync();
        return detail;
    }
}
