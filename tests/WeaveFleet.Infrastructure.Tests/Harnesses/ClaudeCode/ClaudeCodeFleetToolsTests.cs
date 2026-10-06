using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.ClaudeCode;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.ClaudeCode;

/// <summary>
/// Fleet's own tools in a Claude Code session, from Fleet's MCP server: Claude Code calls them
/// <c>mcp__fleet__fleet_…</c>, and Fleet shows, allows and places them under their own names. The fixture
/// (<c>fleet-mcp-tools.jsonl</c>) is claude 2.1.290's output for the agent's own call and a subagent's, made against a
/// scratch Fleet's MCP server.
/// </summary>
public sealed class ClaudeCodeFleetToolsTests
{
    private const string FleetSessionId = "fleet-cc-tools";
    private const string AgentCall = "toolu_01S2RZaCdkoQUQYNqhD4x7tS";
    private const string SubagentStart = "toolu_01E53urD37DxFzCu7v1smi6k";
    private const string SubagentCall = "toolu_01AcL5sdsxnXY65AuifsfcFp";

    // ── Names ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("mcp__fleet__fleet_canvas_open", "fleet_canvas_open")]
    [InlineData("mcp__fleet__fleet_browser_screenshot", "fleet_browser_screenshot")]
    [InlineData("mcp__fleet__fleet_step_done", "fleet_step_done")]
    [InlineData("mcp__github__create_issue", "mcp__github__create_issue")]
    [InlineData("mcp__fleet__", "mcp__fleet__")]
    [InlineData("Bash", "bash")]
    public void Fleets_tools_show_under_their_own_names(string claudeName, string shown)
        => ClaudeCodeTools.Name(claudeName).ShouldBe(shown);

    [Fact]
    public void A_Fleet_tools_input_is_shown_as_the_agent_sent_it()
    {
        var input = JsonDocument.Parse("""{"path":"/tmp/a.html","title":"Options"}""").RootElement;

        ClaudeCodeTools.Input("mcp__fleet__fleet_page_show", input).GetRawText().ShouldBe(input.GetRawText());
    }

    // ── Permissions ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PermissionLevels.Ask)]
    [InlineData(PermissionLevels.Edits)]
    [InlineData(PermissionLevels.All)]
    public void Fleets_tools_never_ask(string level)
    {
        foreach (var tool in FleetToolCatalog.All.Where(tool => tool.Name != "fleet_app_start"))
        {
            var name = ClaudeCodeTools.PermissionName(ClaudeCodeTools.ClaudeName(tool.Name));
            name.ShouldBe(tool.Name);
            new PermissionPolicy(level).Decide(PermissionKinds.Classify(name)).ShouldBe(PermissionReplies.Once, tool.Name);
        }
    }

    [Fact]
    public void Starting_an_app_asks_as_a_shell_command_does()
    {
        var request = new ClaudeCodeControlRequestBody
        {
            Subtype = "can_use_tool",
            ToolName = "mcp__fleet__fleet_app_start",
            Input = JsonDocument.Parse("""{"command":"npm run dev","title":"Shop"}""").RootElement,
            ToolUseId = "toolu_app",
        };

        var ask = ClaudeCodePermissions.ToAsk("r1", "fleet-1", request);

        ask.Kind.ShouldBe(PermissionKinds.Shell);
        ask.Tool.ShouldBe("Bash");
        ask.Title.ShouldBe("npm run dev");
        new PermissionPolicy(PermissionLevels.Ask).Decide(ask.Kind).ShouldBeNull();
        new PermissionPolicy(PermissionLevels.All).Decide(ask.Kind).ShouldBe(PermissionReplies.Once);
    }

    [Fact]
    public void A_process_may_use_every_tool_it_gets_without_asking_but_starting_an_app()
    {
        var all = new FleetToolSwitches(SessionMessages: true, Memory: true, WorkflowStep: true, Browser: true);

        var allowed = ClaudeCodeFleetTools.AllowedWithoutAsking(all).ToList();

        allowed.Count.ShouldBe(FleetToolCatalog.All.Count - 1);
        allowed.ShouldNotContain("mcp__fleet__fleet_app_start");
        allowed.ShouldAllBe(name => name.StartsWith("mcp__fleet__fleet_", StringComparison.Ordinal));
    }

    // ── Placing an MCP call ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_agents_own_call_is_its_sessions()
    {
        var (registry, token) = Registry();
        registry.NoteCall(AgentCall, parentCallId: null);

        var call = (await new ClaudeCodeMcpCalls(registry).PlaceAsync(token, Meta(AgentCall))).ShouldNotBeNull();

        call.ShouldBe(new HarnessMcpCall(FleetSessionId, AgentCall));
        var caller = (await new ClaudeCodeCanvasCallerResolver(registry).ResolveAsync(token, call.HarnessSessionId)).ShouldNotBeNull();
        caller.FleetSessionId.ShouldBe(FleetSessionId);
        caller.ViaParent.ShouldBeFalse();
    }

    [Fact]
    public async Task A_subagents_call_is_its_parents_session_but_marked_as_coming_through_it()
    {
        var (registry, token) = Registry();
        registry.NoteCall(SubagentCall, SubagentStart);

        var call = (await new ClaudeCodeMcpCalls(registry).PlaceAsync(token, Meta(SubagentCall))).ShouldNotBeNull();

        call.ShouldBe(new HarnessMcpCall(SubagentStart, SubagentCall));
        var caller = (await new ClaudeCodeCanvasCallerResolver(registry).ResolveAsync(token, call.HarnessSessionId)).ShouldNotBeNull();
        caller.FleetSessionId.ShouldBe(FleetSessionId);
        caller.ViaParent.ShouldBeTrue();
    }

    [Fact]
    public async Task A_call_Fleet_hasnt_read_yet_is_waited_for()
    {
        var (registry, token) = Registry();
        var placing = new ClaudeCodeMcpCalls(registry, TimeSpan.FromSeconds(5)).PlaceAsync(token, Meta(SubagentCall));

        await Task.Delay(100);
        placing.IsCompleted.ShouldBeFalse();
        registry.NoteCall(SubagentCall, SubagentStart);

        (await placing.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(new HarnessMcpCall(SubagentStart, SubagentCall));
    }

    [Fact]
    public async Task A_call_Fleet_never_reads_counts_as_the_agents_own()
    {
        var (registry, token) = Registry();

        (await new ClaudeCodeMcpCalls(registry, TimeSpan.FromMilliseconds(50)).PlaceAsync(token, Meta("toolu_unseen")))
            .ShouldBe(new HarnessMcpCall(FleetSessionId, "toolu_unseen"));
    }

    [Fact]
    public async Task A_request_with_no_call_id_is_the_session_listing_its_tools()
    {
        var (registry, token) = Registry();

        (await new ClaudeCodeMcpCalls(registry).PlaceAsync(token, default)).ShouldBe(new HarnessMcpCall(FleetSessionId, null));
        (await new ClaudeCodeMcpCalls(registry).PlaceAsync(token, JsonDocument.Parse("""{"claudecode/toolUseId":7}""").RootElement))
            .ShouldBe(new HarnessMcpCall(FleetSessionId, null));
    }

    [Fact]
    public async Task Another_harnesss_token_is_not_placed()
    {
        var (registry, _) = Registry();

        (await new ClaudeCodeMcpCalls(registry).PlaceAsync("someone-elses", Meta(AgentCall))).ShouldBeNull();
    }

    // ── Recorded output ────────────────────────────────────────────────────────

    [Fact]
    public async Task Recorded_calls_show_under_Fleets_names_with_what_Fleets_server_kept_for_them()
    {
        var (registry, _) = Registry();
        var records = new FleetToolCallRecords();
        var metadata = JsonDocument.Parse("""{"canvasId":"cv_01M4955T6VP903WQWAPD7A1ZEZ","version":1}""").RootElement;
        records.Record(AgentCall, new FleetToolCallRecord("Flow · +2 boxes, +1 edge · v1", metadata));
        var messages = new InMemoryMessageRepository();

        await PumpAsync(messages, registry, records, File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ClaudeCode", "fleet-mcp-tools.jsonl")));

        var open = Conversation(messages).SelectMany(m => m.Parts).OfType<ToolUsePart>().Single(tool => tool.ToolCallId == AgentCall);
        open.ToolName.ShouldBe("fleet_canvas_open");
        open.State.ShouldBe(ToolUseState.Completed);
        open.Arguments.GetProperty("kind").GetString().ShouldBe("diagram");
        open.Title.ShouldBe("Flow · +2 boxes, +1 edge · v1");
        open.Metadata!.Value.GetProperty("canvasId").GetString().ShouldBe("cv_01M4955T6VP903WQWAPD7A1ZEZ");
        records.Take(AgentCall).ShouldBeNull();

        // Fleet read who made each call before Claude Code ran it, for its MCP server to place it.
        registry.ParentOf(AgentCall).ShouldBe(string.Empty);
        registry.ParentOf(SubagentCall).ShouldBe(SubagentStart);
        registry.ParentOf(SubagentStart).ShouldBeNull();
    }

    // -----------------------------------------------------------------------

    private static (ClaudeCodeBridgeTokenRegistry Registry, string Token) Registry()
    {
        var registry = new ClaudeCodeBridgeTokenRegistry();
        return (registry, registry.Issue(FleetSessionId, TestUserContext.DefaultUserId));
    }

    private static JsonElement Meta(string callId)
        => JsonDocument.Parse($$"""{"claudecode/toolUseId":"{{callId}}","progressToken":2}""").RootElement;

    private static async Task PumpAsync(
        InMemoryMessageRepository messages, ClaudeCodeBridgeTokenRegistry registry, FleetToolCallRecords records, string[] lines)
    {
        var sessions = new InMemorySessionRepository();
        var connections = new FakeDbConnectionFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageRepository>(messages);
        services.AddSingleton<ISessionRepository>(sessions);
        services.AddSingleton<IDbConnectionFactory>(connections);
        services.AddSingleton(records);
        services.AddSingleton(new SessionActivityWriteService(
            connections, messages, new InMemoryDelegationRepository(), sessions, new InMemorySmartLinkRepository(), new InMemoryOutboxRepository(), new FakeOutboxDispatcher()));

        await using var session = new ClaudeCodeHarnessSession(
            instanceId: "test-instance",
            fleetSessionId: FleetSessionId,
            workingDirectory: "/work",
            config: new ClaudeCodeOptions { BinaryPath = "/nonexistent/claude" },
            environmentVariables: new Dictionary<string, string>(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            scopeFactory: services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ClaudeCodeHarnessSession>.Instance,
            loggerFactory: NullLoggerFactory.Instance,
            ownerUserId: TestUserContext.DefaultUserId,
            bridgeTokens: registry);

        var stdout = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n")));
        await using (var process = new ClaudeCodeProcessManager(NullLogger<ClaudeCodeProcessManager>.Instance))
            await session.PumpStdoutAsync(stdout, process);
        await session.StopAsync(CancellationToken.None);
    }

    private static List<HarnessMessage> Conversation(InMemoryMessageRepository messages)
        => MessagePersistenceService.ToHarnessMessages(
                messages.All.Where(m => m.Role == "assistant").OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            .ToList();
}
