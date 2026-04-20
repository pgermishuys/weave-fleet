using Microsoft.Playwright;

namespace WeaveFleet.E2E.Pages;

/// <summary>
/// Page object for the Session Detail page ("/sessions/{id}?instanceId=...").
/// </summary>
public sealed class SessionDetailPage(IPage page)
{
    private readonly IPage _page = page;

    // ── Selectors ────────────────────────────────────────────────────────────

    private ILocator ActivityStream => _page.GetByTestId("activity-stream");
    private ILocator PromptInput => _page.GetByTestId("prompt-input");
    private ILocator SendButton => _page.GetByTestId("prompt-send-button");
    private ILocator StatusIndicator => _page.GetByTestId("session-status-indicator");
    private ILocator AbortButton => _page.GetByTestId("abort-button");
    private ILocator MessageItems => _page.GetByTestId("message-item");
    private ILocator ArchivedBanner => _page.GetByTestId("session-archived-banner");
    private ILocator ArchivedBadge => _page.GetByTestId("session-archived-badge");
    private ILocator UnarchiveButton => _page.GetByTestId("session-unarchive-button");
    private ILocator UnarchiveBannerButton => _page.GetByTestId("session-unarchive-banner-button");
    private ILocator ArchiveBannerButton => _page.GetByTestId("session-archive-banner-button");
    private ILocator StoppedBanner => _page.GetByTestId("session-stopped-banner");
    private ILocator StopButton => _page.GetByTestId("session-stop-button");
    private ILocator ResumeButton => _page.GetByTestId("session-resume-button");
    private ILocator DeleteButton => _page.GetByTestId("session-delete-button");
    private ILocator ArchivedForkButton => _page.GetByTestId("session-archived-fork-button");
    private ILocator DeleteDialogConfirm => _page.GetByTestId("delete-dialog-confirm");
    private ILocator ForkDialog => _page.GetByTestId("fork-session-dialog");
    private ILocator ForkDialogTitle => _page.GetByTestId("fork-session-dialog-title");
    private ILocator ForkSourceTitle => _page.GetByTestId("fork-session-source-title");
    private ILocator ForkSubmitButton => _page.GetByTestId("fork-session-submit");

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>Navigate directly to a session detail page.</summary>
    public async Task GotoAsync(string sessionId, string instanceId)
    {
        await _page.GotoAsync($"/sessions/{Uri.EscapeDataString(sessionId)}?instanceId={Uri.EscapeDataString(instanceId)}");
        await WaitForLoadedAsync();
    }

    /// <summary>Wait for the activity stream to be visible (page loaded).</summary>
    public Task WaitForLoadedAsync()
        => ActivityStream.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

    // ── Session Status ────────────────────────────────────────────────────────

    /// <summary>Get the current session status ("working" or "idle").</summary>
    public async Task<string?> GetStatusAsync()
        => await StatusIndicator.GetAttributeAsync("data-status");

    /// <summary>Wait for the session status to become "idle".</summary>
    public async Task WaitForIdleAsync(int timeoutMs = 5_000)
        => await Assertions.Expect(StatusIndicator).ToHaveAttributeAsync("data-status", "idle",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = timeoutMs });

    /// <summary>Wait for the session status to become "working".</summary>
    public async Task WaitForBusyAsync(int timeoutMs = 5_000)
        => await Assertions.Expect(StatusIndicator).ToHaveAttributeAsync("data-status", "working",
            new LocatorAssertionsToHaveAttributeOptions { Timeout = timeoutMs });

    // ── Messages ──────────────────────────────────────────────────────────────

    /// <summary>Get all visible message items.</summary>
    public async Task<IReadOnlyList<ILocator>> GetMessageItemsAsync()
    {
        var count = await MessageItems.CountAsync();
        var result = new List<ILocator>(count);
        for (var i = 0; i < count; i++)
            result.Add(MessageItems.Nth(i));
        return result;
    }

    /// <summary>Get message items filtered by role ("user" or "assistant").</summary>
    public ILocator GetMessagesByRole(string role)
        => _page.Locator($"[data-testid='message-item'][data-role='{role}']");

    /// <summary>Wait for at least N messages to appear.</summary>
    public async Task WaitForMessageCountAsync(int minimumCount, int timeoutMs = 5_000)
        => await Assertions.Expect(MessageItems.Nth(minimumCount - 1)).ToBeAttachedAsync(
            new LocatorAssertionsToBeAttachedOptions { Timeout = timeoutMs });

    /// <summary>
    /// Wait for any message item containing the given text to appear.
    /// Uses Playwright auto-waiting — no Thread.Sleep needed.
    /// </summary>
    public async Task WaitForMessageTextAsync(string text, int timeoutMs = 5_000)
        => await Assertions.Expect(MessageItems.Filter(new LocatorFilterOptions { HasText = text }))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = timeoutMs });

    /// <summary>Get the sender name displayed on a specific message item (e.g. "You", "Loom", "Assistant").</summary>
    public static async Task<string?> GetMessageSenderNameAsync(ILocator messageItem)
        => (await messageItem.GetByTestId("message-sender-name").TextContentAsync())?.Trim();

    /// <summary>Get all sender names displayed on messages with the given role.</summary>
    public async Task<IReadOnlyList<string?>> GetSenderNamesByRoleAsync(string role)
    {
        var messages = GetMessagesByRole(role);
        var count = await messages.CountAsync();
        var names = new List<string?>(count);
        for (var i = 0; i < count; i++)
            names.Add(await GetMessageSenderNameAsync(messages.Nth(i)));
        return names;
    }

    // ── Prompt / Send ─────────────────────────────────────────────────────────

    /// <summary>Type text into the prompt input and send it.</summary>
    public async Task SendPromptAsync(string text)
    {
        await PromptInput.FillAsync(text);
        await SendButton.ClickAsync();
    }

    /// <summary>Wait for the send button to become enabled (session is idle and input is non-empty).</summary>
    public Task WaitForSendEnabledAsync(int timeoutMs = 5_000)
        => Assertions.Expect(SendButton).ToBeEnabledAsync(
            new LocatorAssertionsToBeEnabledOptions { Timeout = timeoutMs });

    /// <summary>Wait for the prompt input to become disabled.</summary>
    public Task WaitForPromptDisabledAsync(int timeoutMs = 5_000)
        => Assertions.Expect(PromptInput).ToBeDisabledAsync(
            new LocatorAssertionsToBeDisabledOptions { Timeout = timeoutMs });

    /// <summary>Wait for the archived banner to be visible.</summary>
    public Task WaitForArchivedBannerAsync(int timeoutMs = 5_000)
        => ArchivedBanner.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs
        });

    /// <summary>Check whether the archived badge is visible in the header.</summary>
    public Task<bool> IsArchivedBadgeVisibleAsync() => ArchivedBadge.IsVisibleAsync();

    /// <summary>Click the unarchive action on the detail page.</summary>
    public Task ClickUnarchiveAsync() => UnarchiveButton.ClickAsync();

    /// <summary>Click the archive action shown on stopped/disconnected banners.</summary>
    public Task ClickArchiveAsync() => ArchiveBannerButton.ClickAsync();

    /// <summary>Click the stop button in detail header.</summary>
    public Task ClickStopAsync() => StopButton.ClickAsync();

    /// <summary>Confirm the stop action in detail header.</summary>
    public async Task ConfirmStopAsync()
    {
        var stopConfirmButton = _page.GetByTestId("session-stop-confirm-button");
        try
        {
            await stopConfirmButton.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 500,
            });
            await stopConfirmButton.ClickAsync();
        }
        catch (TimeoutException)
        {
            // Some flows stop immediately without requiring confirmation.
        }
    }

    /// <summary>Wait for the stopped-session banner text.</summary>
    public Task WaitForStoppedBannerAsync()
        => StoppedBanner.WaitForAsync();

    /// <summary>Click the resume button from the stopped banner.</summary>
    public Task ClickResumeAsync() => ResumeButton.ClickAsync();

    /// <summary>Click permanent delete on the detail page.</summary>
    public Task ClickPermanentDeleteAsync() => DeleteButton.ClickAsync();

    /// <summary>Confirm the delete dialog.</summary>
    public Task ConfirmDeleteAsync() => DeleteDialogConfirm.ClickAsync();

    /// <summary>Click New context window from the detail page.</summary>
    public Task ClickNewContextWindowAsync() => ArchivedForkButton.ClickAsync();

    /// <summary>Wait for the fork/new context dialog to appear.</summary>
    public Task WaitForForkDialogAsync()
        => ForkDialogTitle.WaitForAsync();

    /// <summary>Wait for the unarchive banner action to be visible.</summary>
    public Task WaitForUnarchiveBannerButtonAsync() => UnarchiveBannerButton.WaitForAsync();

    /// <summary>Get the source-session title shown in the fork dialog.</summary>
    public Task<string?> GetForkSourceTitleAsync() => ForkSourceTitle.TextContentAsync();

    /// <summary>Fill the fork title field.</summary>
    public Task SetForkTitleAsync(string title) => _page.GetByTestId("fork-session-title-input").FillAsync(title);

    /// <summary>Submit the fork/new context dialog and wait for session navigation.</summary>
    public async Task SubmitForkAsync()
    {
        var previousUrl = _page.Url;
        await ForkSubmitButton.ClickAsync();
        await _page.WaitForURLAsync(
            url => url.Contains("/sessions/") && !string.Equals(url, previousUrl, StringComparison.Ordinal),
            new PageWaitForURLOptions { Timeout = 5_000 });
    }

    /// <summary>Get the archived banner text.</summary>
    public Task<string?> GetArchivedBannerTextAsync() => ArchivedBanner.TextContentAsync();

    // ── Abort ─────────────────────────────────────────────────────────────────

    /// <summary>Click the abort button. Only visible when session is busy (desktop view).</summary>
    public Task ClickAbortAsync() => AbortButton.ClickAsync();

    /// <summary>Check whether the abort button is currently visible.</summary>
    public Task<bool> IsAbortVisibleAsync() => AbortButton.IsVisibleAsync();
}
