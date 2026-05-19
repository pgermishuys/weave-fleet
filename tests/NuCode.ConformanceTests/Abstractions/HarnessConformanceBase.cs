using FakeLlmServer;

namespace NuCode.ConformanceTests.Abstractions;

/// <summary>
/// Abstract base class for harness conformance tests.
/// Subclasses provide a concrete <see cref="IHarnessSessionFixture"/> via <see cref="CreateFixture"/>.
/// The same test methods run against both NuCode and OpenCode harnesses.
/// </summary>
public abstract class HarnessConformanceBase : IAsyncLifetime
{
    private IHarnessSessionFixture _fixture = null!;
    private IHarnessSession _session = null!;
    private string _workDir = null!;

    /// <summary>Creates the fixture for this harness implementation.</summary>
    protected abstract IHarnessSessionFixture CreateFixture();

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"conformance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _fixture = CreateFixture();
        _session = await _fixture.CreateSessionAsync(_workDir);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await _session.DisposeAsync();
        await _fixture.DisposeAsync();
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Enqueues a simple text response and sends a prompt, waiting for idle.</summary>
    protected async Task SendPromptAndWaitAsync(string prompt, string responseText = "Hello!", CancellationToken ct = default)
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = responseText });
        await _session.SendPromptAsync(prompt, null, ct);
    }

    /// <summary>Collects events from SubscribeAsync until the predicate is satisfied or timeout.</summary>
    protected static Task<List<HarnessEvent>> CollectEventsAsync(
        IHarnessSession session,
        Func<List<HarnessEvent>, bool> until,
        TimeSpan? timeout = null)
        => EventCollector.CollectAsync(session, until, timeout);

    // ── Task 7: Core properties and health ───────────────────────────────────

    [Fact]
    public void InstanceId_IsNotEmpty()
    {
        _session.InstanceId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HarnessType_IsNotEmpty()
    {
        _session.HarnessType.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Status_IsIdle_Initially()
    {
        _session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task CheckHealthAsync_ReturnsHealthy()
    {
        var result = await _session.CheckHealthAsync(CancellationToken.None);
        result.Healthy.ShouldBeTrue();
    }

    // ── Task 8: SendPromptAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SendPromptAsync_TransitionsToRunning_ThenBackToIdle()
    {
        var statusHistory = new List<HarnessSessionStatus>();

        // Subscribe in background to capture status transitions
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in _session.SubscribeAsync(cts.Token))
            {
                if (evt.Type is "session.busy" or "session.idle")
                {
                    statusHistory.Add(evt.Type == "session.busy"
                        ? HarnessSessionStatus.Running
                        : HarnessSessionStatus.Idle);
                }

                if (evt.Type == "session.idle" && statusHistory.Count >= 2)
                    break;
            }
        }, cts.Token);

        await SendPromptAndWaitAsync("Hello");
        cts.Cancel();

        try { await subscribeTask; } catch (OperationCanceledException) { }

        statusHistory.ShouldContain(HarnessSessionStatus.Running);
        statusHistory.ShouldContain(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task SendPromptAsync_CreatesUserAndAssistantMessages()
    {
        await SendPromptAndWaitAsync("Hello");

        var page = await _session.GetMessagesAsync(new MessageQuery(Limit: 10), CancellationToken.None);
        page.Messages.ShouldContain(m => m.Role == "user");
        page.Messages.ShouldContain(m => m.Role == "assistant");
    }

    [Fact]
    public async Task ResumeToken_IsPopulated_AfterFirstPrompt()
    {
        _session.ResumeToken.ShouldBeNull();

        await SendPromptAndWaitAsync("Hello");

        _session.ResumeToken.ShouldNotBeNullOrWhiteSpace();
    }

    // ── Task 9: AbortAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task AbortAsync_SetsStatusToIdle()
    {
        await _session.AbortAsync(CancellationToken.None);
        _session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    // ── Task 10: GetMessagesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesAsync_ReturnsEmptyPage_BeforeAnyPrompt()
    {
        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        page.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessages_AfterPrompt()
    {
        await SendPromptAndWaitAsync("Hello");

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        page.Messages.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetMessagesAsync_MessagesHaveCorrectRoles()
    {
        await SendPromptAndWaitAsync("Hello");

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var roles = page.Messages.Select(m => m.Role).ToList();
        roles.ShouldContain("user");
        roles.ShouldContain("assistant");
    }

    // ── Task 11: StopAsync and DeleteAsync ────────────────────────────────────

    [Fact]
    public async Task StopAsync_SetsStatusToStopped()
    {
        await _session.StopAsync(CancellationToken.None);
        _session.Status.ShouldBe(HarnessSessionStatus.Stopped);
    }

    [Fact]
    public async Task DeleteAsync_SetsStatusToStopped()
    {
        await _session.DeleteAsync(CancellationToken.None);
        _session.Status.ShouldBe(HarnessSessionStatus.Stopped);
    }

    // ── Task 12: Message parts ────────────────────────────────────────────────

    [Fact]
    public async Task TextPart_IsMappedCorrectly()
    {
        const string expectedText = "This is a test response from the fake LLM.";
        await SendPromptAndWaitAsync("Hello", expectedText);

        var page = await _session.GetMessagesAsync(null, CancellationToken.None);
        var assistantMsg = page.Messages.FirstOrDefault(m => m.Role == "assistant");
        assistantMsg.ShouldNotBeNull();
        assistantMsg.TextContent.ShouldContain(expectedText);
    }

    // ── Task 13: Event streaming ──────────────────────────────────────────────

    [Fact]
    public async Task SubscribeAsync_EmitsSessionBusy_WhenPromptStarts()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.busy"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "session.busy");
    }

    [Fact]
    public async Task SubscribeAsync_EmitsSessionIdle_WhenPromptCompletes()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "session.idle");
    }

    [Fact]
    public async Task SubscribeAsync_EmitsMessageCreated_ForUserMessage()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "message.created"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "message.created");
    }

    [Fact]
    public async Task SubscribeAsync_EmitsPartUpdated_ForParts()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "message.part.updated"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "message.part.updated");
    }

    [Fact]
    public async Task SubscribeAsync_EventsHaveCorrectSessionId()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldAllBe(e => !string.IsNullOrWhiteSpace(e.SessionId));
    }

    // ── Task 14: Configuration ────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentsAsync_ReturnsAtLeastOneAgent()
    {
        var agents = await _session.GetAgentsAsync(CancellationToken.None);
        agents.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetProvidersAsync_ReturnsAtLeastOneProvider()
    {
        var providers = await _session.GetProvidersAsync(CancellationToken.None);
        providers.ShouldNotBeEmpty();
    }

    // ── Session lifecycle events ──────────────────────────────────────────────

    [Fact]
    public async Task SubscribeAsync_EmitsSessionCreated_OnFirstPrompt()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "session.created");
    }

    [Fact]
    public async Task SubscribeAsync_EmitsSessionUpdated_OnFirstPrompt()
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => e.Type == "session.updated");
    }
}
