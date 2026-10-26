using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Automations;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.EventBus;
using WeaveFleet.Infrastructure.GitHub;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// GitHub links in what Claude Code writes reach <see cref="SmartLinkDetector"/> by whichever road Fleet sends them:
/// the relay's event stream (text the session's own turn streamed) or the outbox (a subagent's text, every tool call).
/// </summary>
#pragma warning disable CA1001 // Disposal handled by IAsyncLifetime.DisposeAsync
public sealed class ClaudeCodeSmartLinkTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string SessionId = "fleet-cc-links";
    private const string ChildId = "fleet-cc-links-child";
    private const string UserId = TestUserContext.DefaultUserId;

    private readonly InMemoryOutboxRepository _outbox = new();
    private readonly SmartLinkDetector _detector = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ClaudeCodeHarnessSession _session;

    public ClaudeCodeSmartLinkTests()
    {
        var messages = new InMemoryMessageRepository();
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();

        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IOutboxRepository>(_outbox);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(new SessionActivityWriteService(
            connections, messages, new InMemoryDelegationRepository(), sessions, new InMemorySmartLinkRepository(), _outbox, new FakeOutboxDispatcher()));
        _scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: SessionId,
            workingDirectory: "/work",
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: _scopes,
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: UserId)
        {
            ChildSessions = (_, _, _) => Task.FromResult<string?>(ChildId),
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _session.DisposeAsync();

    [Fact]
    public async Task A_subagents_pull_request_and_the_issue_its_reply_names_are_links_on_its_session()
    {
        await RunAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_task","name":"Agent","input":{"description":"Open the PR","prompt":"Open a PR for the fix."}}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_B","role":"assistant","content":[{"type":"tool_use","id":"toolu_gh","name":"Bash","input":{"command":"gh pr create --fill --base main"}}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_gh","type":"tool_result","content":"https://github.com/acme/widgets/pull/7","is_error":false}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"assistant","message":{"id":"msg_C","role":"assistant","content":[{"type":"text","text":"The PR is open at https://github.com/acme/widgets/pull/7 and fixes https://github.com/acme/widgets/issues/3."}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_task","type":"tool_result","content":"Done."}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_D","role":"assistant","content":[{"type":"text","text":"The subagent opened the PR."}]},"parent_tool_use_id":null}""",
            """{"subtype":"success","is_error":false,"result":"","session_id":"s","type":"result"}""");

        var links = Detected();
        links.ShouldContain((ChildId, "acme/widgets#7", SmartLinkRelationships.Own));
        links.ShouldContain((ChildId, "acme/widgets#3", SmartLinkRelationships.Mentioned));
        links.ShouldAllBe(link => link.Session == ChildId);
    }

    [Fact]
    public async Task A_pull_request_the_session_opens_itself_is_its_own()
    {
        await RunAsync(
            """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_gh","name":"Bash","input":{"command":"gh pr create --title \"Fix\" --body \"x\""}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_gh","type":"tool_result","content":"https://github.com/acme/widgets/pull/9","is_error":false}]},"parent_tool_use_id":null}""",
            """{"subtype":"success","is_error":false,"result":"","session_id":"s","type":"result"}""");

        Detected().ShouldContain((SessionId, "acme/widgets#9", SmartLinkRelationships.Own));
    }

    /// <summary>
    /// Runs the pump over <paramref name="lines"/>, then sends what it produced the ways Fleet does: its event stream
    /// past the detector as the relay does, and its outbox through the dispatcher.
    /// </summary>
    private async Task RunAsync(params string[] lines)
    {
        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await _session.PumpStdoutAsync(stdout, process);

        await _session.StopAsync(CancellationToken.None);
        await foreach (var evt in _session.SubscribeAsync(CancellationToken.None))
            _detector.Observe(evt.FleetSessionId ?? SessionId, UserId, evt.Type, evt.Payload);

        var dispatcher = new InProcessOutboxDispatcher(
            _scopes, new FakeEventBroadcaster(), new NoAutomations(), new FleetOptions(), NullLogger<InProcessOutboxDispatcher>.Instance, _detector);
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        _outbox.All.ShouldAllBe(message => message.DispatchedAt != null);
    }

    private List<(string Session, string Resource, string Relationship)> Detected()
    {
        var links = new List<(string, string, string)>();
        while (_detector.Reader.TryRead(out var link))
            links.Add((link.SessionId, link.Reference.ResourceId, link.Relationship));
        return links;
    }

    private sealed class NoAutomations : IAutomationEventNotifier
    {
        public Task NotifyAsync(string eventType, string eventId, string? sessionId, string? sessionSourceReference, string? eventSummary, CancellationToken ct)
            => Task.CompletedTask;
    }
}
