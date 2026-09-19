using System.Runtime.CompilerServices;
using System.Text.Json;
using FakeLlmServer;

namespace WeaveFleet.ConformanceTests.Abstractions;

/// <summary>
/// Abstract base class for harness conformance tests.
/// Subclasses provide a concrete <see cref="IHarnessSessionFixture"/> via <see cref="CreateFixture"/>.
/// The same test methods run against every harness that provides a fixture.
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

    /// <summary>
    /// Why this harness can't meet one of the shared tests, by test method name. A listed test is skipped with the
    /// reason for this harness only; every other harness still runs it.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> NotApplicable { get; } = new Dictionary<string, string>();

    /// <summary>Skips the calling test when <see cref="NotApplicable"/> lists it.</summary>
    protected void SkipWhenNotApplicable([CallerMemberName] string test = "")
    {
        if (NotApplicable.TryGetValue(test, out var reason))
            throw new InvalidOperationException(Xunit.v3.DynamicSkipToken.Value + reason);
    }

    /// <summary>Enqueues a simple text response, sends a prompt and waits until the turn is over.</summary>
    protected async Task SendPromptAndWaitAsync(string prompt, string responseText = "Hello!", CancellationToken ct = default)
    {
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = responseText });

        // Subscribed before the prompt: a harness may answer before SendPromptAsync returns.
        var idle = CollectEventsAsync(_session, evts => evts.Any(e => e.Type == EventTypes.SessionIdle), TimeSpan.FromSeconds(30));
        await _session.SendPromptAsync(prompt, null, ct);
        (await idle).ShouldContain(e => e.Type == EventTypes.SessionIdle, "The turn didn't end within 30 seconds.");
    }

    /// <summary>A harness says a turn started with <c>session.status</c> <c>busy</c>.</summary>
    protected static bool IsBusy(HarnessEvent evt)
        => evt.Type == EventTypes.SessionStatus
            && evt.Payload is { ValueKind: JsonValueKind.Object } payload
            && payload.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.Object
            && status.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == ActivityStatuses.Busy;

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
                if (IsBusy(evt) || evt.Type == EventTypes.SessionIdle)
                {
                    statusHistory.Add(IsBusy(evt)
                        ? HarnessSessionStatus.Running
                        : HarnessSessionStatus.Idle);
                }

                if (evt.Type == EventTypes.SessionIdle && statusHistory.Count >= 2)
                    break;
            }
        }, cts.Token);

        // Allow the background subscription to start iterating before sending the prompt.
        await Task.Delay(200);

        // The subscription above waits for the turn to end: a second one would compete with it for the events.
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hello!" });
        await _session.SendPromptAsync("Hello", null, CancellationToken.None);

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
        SkipWhenNotApplicable();
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
            evts => evts.Any(IsBusy),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        events.ShouldContain(e => IsBusy(e));
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
        SkipWhenNotApplicable();
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
        SkipWhenNotApplicable();
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
    public async Task SubscribeAsync_EmitsSessionCreatedAndBusy_OnFirstPrompt()
    {
        SkipWhenNotApplicable();
        _fixture.EnqueueResponse(new ScriptedLlmResponse { Text = "Hi!" });

        var eventsTask = CollectEventsAsync(
            _session,
            evts => evts.Any(e => e.Type == "session.idle"),
            TimeSpan.FromSeconds(10));

        await _session.SendPromptAsync("Hello", null, CancellationToken.None);
        var events = await eventsTask;

        // session.updated is intentionally NOT emitted on first prompt to avoid
        // overwriting the user-chosen Fleet session title via the persistence projection.
        events.ShouldContain(e => e.Type == "session.created");
        events.ShouldContain(e => IsBusy(e));
    }
}
