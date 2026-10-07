using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// What Claude Code says about what it's doing, fed through <see cref="ClaudeCodeHarnessSession"/>'s pump from recorded
/// stream-json: retries, compactions, usage limits, and what each message used.
/// </summary>
#pragma warning disable CA1001 // Type owns disposable fields — disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeStatusPumpTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-status";

    private readonly InMemoryMessageRepository _messages = new();
    private readonly InMemoryOutboxRepository _outbox = new();
    private readonly ClaudeCodeHarnessSession _session;

    public ClaudeCodeStatusPumpTests()
    {
        var delegations = new InMemoryDelegationRepository();
        var sessions = new InMemorySessionRepository();
        var dispatcher = new FakeOutboxDispatcher();
        var connections = new FakeDbConnectionFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(_messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, _messages, delegations, sessions, new InMemorySmartLinkRepository(), _outbox, dispatcher));

        _session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: SessionId,
            workingDirectory: "/tmp",
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    // ── Retries ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiRetries_ReadAsRetrying_WithTheAttemptWhyAndWhen_ThenBusyWhenClaudeAnswers()
    {
        var events = await PumpAsync(Fixture("api-retry.jsonl"));

        var statuses = events.Where(e => e.Type == EventTypes.SessionStatus).Select(e => e.Payload!.Value.GetProperty("status")).ToList();
        var retries = statuses.Where(s => s.GetProperty("type").GetString() == ActivityStatuses.Retry).ToList();
        retries.Count.ShouldBe(2);
        retries[0].GetProperty("count").GetInt32().ShouldBe(1);
        retries[1].GetProperty("count").GetInt32().ShouldBe(2);
        retries.ShouldAllBe(r => r.GetProperty("max").GetInt32() == 10);
        retries.ShouldAllBe(r => r.GetProperty("reason").GetString() == "API overloaded (529)");
        retries[0].GetProperty("delay").GetInt64().ShouldBe(515);
        retries[1].GetProperty("delay").GetInt64().ShouldBe(1226);

        // The answer that follows the retries puts the session back to working, once.
        var afterRetries = statuses.SkipWhile(s => s.GetProperty("type").GetString() != ActivityStatuses.Retry)
            .SkipWhile(s => s.GetProperty("type").GetString() == ActivityStatuses.Retry)
            .Select(s => s.GetProperty("type").GetString())
            .ToList();
        afterRetries.ShouldBe([ActivityStatuses.Busy]);
        Text().ShouldContain("Hello, let's code.");
    }

    [Fact]
    public async Task AnAnswerWithoutARetry_DoesNotSayBusyAgain()
    {
        var events = await PumpAsync(
            """{"type":"system","subtype":"init","session_id":"cc-1"}""",
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"text","text":"hi"}]},"parent_tool_use_id":null}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"hi","session_id":"cc-1"}""");

        // The init's own wake turn says busy; nothing after it says busy again.
        events.Count(e => e.Type == EventTypes.SessionStatus).ShouldBe(1);
    }

    // ── Compaction ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compact_LeavesADividerWithTheSizesAndTheSummary_WhereItHappened()
    {
        var events = await PumpAsync(Fixture("compact.jsonl"));

        var conversation = Conversation();
        var dividers = conversation.Where(m => m.Parts.Any(p => p is CompactionPart)).ToList();
        var divider = dividers.ShouldHaveSingleItem().Parts.ShouldHaveSingleItem().ShouldBeOfType<CompactionPart>();
        divider.Trigger.ShouldBe(ContextCompactionTriggers.Manual);
        divider.TokensBefore.ShouldBe(35_438);
        divider.TokensAfter.ShouldBe(1_450);

        // The summary without Claude Code's framing: no preamble, no transcript path, no "Continue the conversation".
        divider.Summary.ShouldNotBeNull().ShouldStartWith("1. Primary Request and Intent:");
        divider.Summary.ShouldContain("9. Optional Next Step:");
        divider.Summary.ShouldNotContain("This session is being continued");
        divider.Summary.ShouldNotContain("Continue the conversation");
        divider.Summary.ShouldNotContain("<CLAUDE_CONFIG_DIR>");

        // It sits between the first answer and the last.
        var index = conversation.IndexOf(dividers[0]);
        conversation.Take(index).ShouldContain(m => m.TextContent.Contains("octopus", StringComparison.OrdinalIgnoreCase));
        conversation.Skip(index + 1).ShouldContain(m => m.TextContent.Contains("README.md", StringComparison.Ordinal));

        // The summary isn't shown as a message of its own, and the ring still hears about the compaction.
        Text().ShouldNotContain(t => t.Contains("This session is being continued", StringComparison.Ordinal));
        events.Where(e => e.Type == EventTypes.ContextCompaction).Select(e => ContextEvents.ReadCompaction(e)!.Phase)
            .ShouldBe([ContextCompactionPhases.Started, ContextCompactionPhases.Ended]);

        // The page saw the divider go out with its summary.
        var dividerUpdates = PartUpdates().Where(p => p.GetProperty("type").GetString() == "compaction").ToList();
        dividerUpdates.Count.ShouldBe(2);
        dividerUpdates[0].TryGetProperty("summary", out _).ShouldBeFalse();
        dividerUpdates[1].GetProperty("tokensBefore").GetInt32().ShouldBe(35_438);
        dividerUpdates[1].GetProperty("summary").GetString().ShouldNotBeNull().ShouldStartWith("1. Primary Request");
    }

    [Fact]
    public async Task ASyntheticLineWithNoCompactionBeforeIt_IsNotTakenForASummary()
    {
        await PumpAsync(
            """{"type":"system","subtype":"init","session_id":"cc-1"}""",
            """{"type":"user","message":{"role":"user","content":"This session is being continued from a previous conversation.\n\nSummary:\nstray"},"parent_tool_use_id":null,"isSynthetic":true}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"","session_id":"cc-1"}""");

        Conversation().SelectMany(m => m.Parts).OfType<CompactionPart>().ShouldBeEmpty();
    }

    // ── Usage limits ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RateLimitEvent_ReportsTheAccountsWindows_NotTheConversation()
    {
        var events = await PumpAsync(Fixture("rate-limit-event.jsonl"));

        var report = events.Where(e => e.Type == EventTypes.HarnessUsage).Select(UsageLimitEvents.Read).ShouldHaveSingleItem().ShouldNotBeNull();
        var windows = report.Windows.ToDictionary(w => w.Window);
        windows.Keys.Order().ShouldBe([UsageLimitWindows.FiveHour, UsageLimitWindows.SevenDay]);
        windows[UsageLimitWindows.FiveHour].Utilization.ShouldBe(0.82);
        windows[UsageLimitWindows.FiveHour].ResetsAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1791344624));
        windows[UsageLimitWindows.FiveHour].Status.ShouldBe(UsageLimitStatuses.Allowed);
        // The window the event's status is about gets it.
        windows[UsageLimitWindows.SevenDay].Status.ShouldBe(UsageLimitStatuses.Warning);
        windows[UsageLimitWindows.SevenDay].Utilization.ShouldBe(0.63);

        Text().ShouldBe(["Hey, let's code!"]);
    }

    // ── What each message used ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachMessage_ReportsWhatItUsedOnce_AndTheLastTheTurnsCost()
    {
        await PumpAsync(Fixture("two-tools.jsonl"));

        var steps = Conversation().Select(m => m.Parts.OfType<StepFinishPart>().ToList()).ToList();
        steps.ShouldAllBe(s => s.Count == 1);
        // Claude Code repeats a call's usage on each of its lines: each message counts its call once.
        steps.Select(s => s[0].TokensInput).ShouldAllBe(input => input > 0);
        // Only the turn's last message carries what the turn cost.
        steps.Take(steps.Count - 1).ShouldAllBe(s => s[0].Cost == 0);
        steps[^1][0].Cost.ShouldBeGreaterThan(0);
        // The last call's real output count, from the result.
        steps[^1][0].TokensOutput.ShouldBe(60);
    }

    [Fact]
    public async Task StreamedMessages_TakeTheirRealOutputFromTheStream_AndTheCostOfEachTurnNotTheProcessTotal()
    {
        // Three turns on one process: a prompt, /compact, a prompt. Claude Code's result says what the process has cost so
        // far (0.0122, 0.0929, 0.0996), so each turn's is the difference.
        await PumpAsync(Fixture("compact.jsonl"));

        var answers = Conversation().Where(m => m.Parts.OfType<StepFinishPart>().Any()).ToList();
        var steps = answers.Select(m => m.Parts.OfType<StepFinishPart>().ShouldHaveSingleItem()).ToList();
        steps.Count.ShouldBe(3);
        // From the stream's message_delta: 169 out, 92 of them thinking (the assistant line says 4).
        steps[0].TokensOutput.ShouldBe(169 - 92);
        steps[0].TokensReasoning.ShouldBe(92);
        steps[0].TokensInput.ShouldBe(10 + 3028);
        steps[0].Cost.ShouldBe(0);
        steps[1].Cost.ShouldBe(0.01216325, 1e-9);
        steps[2].Cost.ShouldBe(0.09957185 - 0.09289125, 1e-9);
        steps.Sum(s => s.Cost).ShouldBeLessThan(0.09957185 - 0.09289125 + 0.01216325 + 1e-9);
    }

    [Fact]
    public async Task StepFinishParts_ReachThePage_AsStepFinishUpdates()
    {
        await PumpAsync(Fixture("two-tools.jsonl"));

        var steps = PartUpdates().Where(p => p.GetProperty("type").GetString() == "step-finish").ToList();
        steps.Count.ShouldBe(Conversation().Count);
        steps.ShouldAllBe(p => p.GetProperty("tokens").GetProperty("input").GetDouble() > 0);
        steps[^1].GetProperty("cost").GetDouble().ShouldBeGreaterThan(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static string[] Fixture(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", name));

    /// <summary>Runs the pump over <paramref name="lines"/> and returns the events it published.</summary>
    private async Task<List<HarnessEvent>> PumpAsync(params string[] lines)
    {
        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await _session.PumpStdoutAsync(stdout, process);

        await _session.StopAsync(CancellationToken.None);
        var events = new List<HarnessEvent>();
        await foreach (var evt in _session.SubscribeAsync(CancellationToken.None))
            events.Add(evt);

        return events;
    }

    /// <summary>The assistant messages as the page reads them back, oldest first.</summary>
    private List<HarnessMessage> Conversation()
        => MessagePersistenceService.ToHarnessMessages(
                _messages.All.Where(m => m.Role == "assistant").OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();

    private List<string> Text()
        => Conversation().SelectMany(m => m.Parts).OfType<TextPart>().Select(t => t.Text).ToList();

    /// <summary>The parts the page was sent, in order.</summary>
    private List<JsonElement> PartUpdates()
        => _outbox.All
            .Where(m => m.Type == EventTypes.MessagePartUpdated)
            .Select(m => JsonDocument.Parse(m.Payload).RootElement.GetProperty("part"))
            .ToList();
}
