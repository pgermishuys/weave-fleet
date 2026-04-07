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
        => await messageItem.GetByTestId("message-sender-name").TextContentAsync();

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

    // ── Abort ─────────────────────────────────────────────────────────────────

    /// <summary>Click the abort button. Only visible when session is busy (desktop view).</summary>
    public Task ClickAbortAsync() => AbortButton.ClickAsync();

    /// <summary>Check whether the abort button is currently visible.</summary>
    public Task<bool> IsAbortVisibleAsync() => AbortButton.IsVisibleAsync();
}
