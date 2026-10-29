using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Application.FleetTools;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Identity;
using WeaveFleet.Infrastructure.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.Acp;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.Acp;

public sealed class AcpHarnessSessionTests
{
    private static AcpHarnessSession NewSession(FakeAcpAgent agent) =>
        new("copilot", "copilot-1", "fleet-1", "acp-1", "/work", agent, NullLogger.Instance);

    private static async Task<List<HarnessEvent>> ReadAsync(AcpHarnessSession session, Func<List<HarnessEvent>, bool> until)
    {
        var seen = new List<HarnessEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in session.SubscribeAsync(timeout.Token))
        {
            seen.Add(evt);
            if (until(seen))
                break;
        }

        return seen;
    }

    private static bool Idle(List<HarnessEvent> seen)
        => seen.Any(e => e.Type == EventTypes.SessionStatus && e.Payload!.Value.GetProperty("status").GetProperty("type").GetString() == "idle");

    private sealed class Tokens : IAnalyticsCollector
    {
        public List<TokenEventData> Events { get; } = [];
        public void AcceptTokenEvent(TokenEventData data) => Events.Add(data);
        public void AcceptSessionSnapshot(SessionSnapshotData data) { }
    }

    [Fact]
    public async Task a_turns_usage_reaches_the_analytics_collector_so_the_status_bar_counts_it()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["stopReason"] = "end_turn",
            ["usage"] = new JsonObject { ["inputTokens"] = 1000, ["outputTokens"] = 120, ["thoughtTokens"] = 20, ["cachedReadTokens"] = 600, ["cachedWriteTokens"] = 100, ["credits"] = 0.4 },
        });
        var tokens = new Tokens();
        await using var session = new AcpHarnessSession("copilot", "copilot-1", "fleet-1", "acp-1", "/work", agent, NullLogger.Instance, tokens, "p1", "Demo");

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        var events = await ReadAsync(session, Idle);

        var counted = tokens.Events.Single();
        (counted.SessionId, counted.ProjectName, counted.ProviderId).ShouldBe(("fleet-1", "Demo", "copilot"));
        (counted.TokensInput, counted.TokensOutput, counted.TokensReasoning, counted.TokensCacheRead, counted.TokensCacheWrite).ShouldBe((300d, 100d, 20d, 600d, 100d));
        counted.TokensTotal.ShouldBe(1120d);
        events.Select(e => e.Type).ShouldContain(EventTypes.MessagePartUpdated);
    }

    [Fact]
    public async Task a_turn_without_usage_counts_nothing()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        var tokens = new Tokens();
        await using var session = new AcpHarnessSession("copilot", "copilot-1", "fleet-1", "acp-1", "/work", agent, NullLogger.Instance, tokens);

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        tokens.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_prompt_streams_its_answer_and_ends_idle()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, p) =>
        {
            if (method == "session/prompt")
                agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hi" } });
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        };
        await using var session = NewSession(agent);

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        var events = await ReadAsync(session, Idle);

        events.Select(e => e.Type).ShouldContain(EventTypes.MessagePartUpdated);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        var prompt = agent.Received.Single(r => r.Method == "session/prompt").Params;
        prompt.GetProperty("sessionId").GetString().ShouldBe("acp-1");
        prompt.GetProperty("prompt")[0].GetProperty("text").GetString().ShouldBe("hello");
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.Role).ShouldBe(["user", "assistant"]);
    }

    [Fact]
    public async Task the_prompt_keeps_fleets_message_id_and_the_answer_sorts_after_it()
    {
        // The client places a message by its id: the prompt Fleet shows has Fleet's id, so the answer's has to sort after it.
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, p) =>
        {
            if (method == "session/prompt")
                agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Hi" } });
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        };
        await using var session = NewSession(agent);
        var promptId = AscendingMessageId.New();

        await session.SendPromptAsync("hello", new PromptOptions { MessageId = promptId }, CancellationToken.None);
        await ReadAsync(session, Idle);

        var messages = (await session.GetMessagesAsync(null, CancellationToken.None)).Messages;
        messages[0].Id.ShouldBe(promptId);
        messages[1].Id.ShouldStartWith("msg_");
        string.CompareOrdinal(messages[1].Id, promptId).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task a_permission_ask_waits_for_the_user_and_the_answer_picks_the_matching_option()
    {
        await using var agent = new FakeAcpAgent();
        var answered = new TaskCompletionSource<JsonElement>();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                answered.SetResult(await agent.AskAsync("session/request_permission", Ask()));
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);

        await session.SendPromptAsync("write it", null, CancellationToken.None);
        var asked = (await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.PermissionAsked))).Last();
        var ask = PermissionEvents.ReadAsk(asked)!;
        await session.ReplyToPermissionAsync(ask.Id, PermissionReplies.Once, null, CancellationToken.None);

        ask.Tool.ShouldBe("edit");
        ask.Title.ShouldBe("Write notes.md");
        (await answered.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow");
    }

    [Fact]
    public async Task a_shell_ask_shows_the_command_not_the_agents_description_of_it()
    {
        var request = Ask();
        request["toolCall"] = new JsonObject
        {
            ["toolCallId"] = "c1", ["kind"] = "execute", ["title"] = "Clean up temp files",
            ["rawInput"] = new JsonObject { ["command"] = "rm -rf ~/projects", ["description"] = "Clean up temp files" },
        };

        (await AskedAsync(request, null)).Title.ShouldBe("rm -rf ~/projects");
    }

    [Fact]
    public async Task an_ask_that_carries_only_the_call_id_shows_the_command_the_call_started_with()
    {
        var request = Ask();
        request["toolCall"] = new JsonObject { ["toolCallId"] = "c1" };
        var started = new JsonObject
        {
            ["sessionUpdate"] = "tool_call", ["toolCallId"] = "c1", ["kind"] = "execute", ["title"] = "Tidy up", ["status"] = "pending",
            ["rawInput"] = new JsonObject { ["command"] = "git push --force origin main" },
        };

        (await AskedAsync(request, started)).Title.ShouldBe("git push --force origin main");
    }

    private static async Task<PermissionAsk> AskedAsync(JsonObject request, JsonObject? update)
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = async (method, args) =>
        {
            if (method == "session/prompt")
            {
                if (update is not null)
                    agent.Update("acp-1", update);
                await agent.AskAsync("session/request_permission", request);
            }

            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        await session.SendPromptAsync("go", null, CancellationToken.None);
        var asked = (await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.PermissionAsked))).Last();
        var ask = PermissionEvents.ReadAsk(asked)!;
        await session.ReplyToPermissionAsync(ask.Id, PermissionReplies.Reject, null, CancellationToken.None);
        return ask;
    }

    [Fact]
    public async Task rejecting_picks_the_reject_option()
    {
        await using var agent = new FakeAcpAgent();
        var answered = new TaskCompletionSource<JsonElement>();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                answered.SetResult(await agent.AskAsync("session/request_permission", Ask()));
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);

        await session.SendPromptAsync("write it", null, CancellationToken.None);
        var asked = (await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.PermissionAsked))).Last();
        await session.ReplyToPermissionAsync(PermissionEvents.ReadAsk(asked)!.Id, PermissionReplies.Reject, null, CancellationToken.None);

        (await answered.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("deny");
    }

    [Fact]
    public async Task a_level_that_allows_the_edit_answers_without_asking()
    {
        await using var agent = new FakeAcpAgent();
        var answered = new TaskCompletionSource<JsonElement>();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                answered.SetResult(await agent.AskAsync("session/request_permission", Ask()));
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Edits), CancellationToken.None);

        await session.SendPromptAsync("write it", null, CancellationToken.None);
        var events = await ReadAsync(session, Idle);

        events.ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
        (await answered.Task).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow");
    }

    [Theory]
    [InlineData(PermissionLevels.Ask, "edit", null, true)]
    [InlineData(PermissionLevels.Ask, "execute", null, true)]
    [InlineData(PermissionLevels.Ask, "read", null, false)]
    [InlineData(PermissionLevels.Ask, "other", "fleet_page_show", false)]
    [InlineData(PermissionLevels.Ask, "other", "some_mcp_tool", true)]
    [InlineData(PermissionLevels.Edits, "edit", null, false)]
    [InlineData(PermissionLevels.Edits, "execute", null, true)]
    [InlineData(PermissionLevels.All, "edit", null, false)]
    [InlineData(PermissionLevels.All, "execute", null, false)]
    [InlineData(PermissionLevels.All, "other", "some_mcp_tool", false)]
    public async Task each_level_asks_for_what_it_should_and_answers_the_rest(string level, string kind, string? realName, bool asks)
    {
        await using var agent = new FakeAcpAgent();
        var answered = new TaskCompletionSource<JsonElement>();
        var request = Ask();
        request["toolCall"]!["kind"] = kind;
        if (realName is not null)
            request["toolCall"]!["_meta"] = new JsonObject { ["toolName"] = realName };
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                answered.SetResult(await agent.AskAsync("session/request_permission", request));
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.ApplyPermissionsAsync(new PermissionPolicy(level), CancellationToken.None);

        await session.SendPromptAsync("go", null, CancellationToken.None);
        if (asks)
        {
            var asked = (await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.PermissionAsked))).Last();
            await session.ReplyToPermissionAsync(PermissionEvents.ReadAsk(asked)!.Id, PermissionReplies.Reject, null, CancellationToken.None);
            (await answered.Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("deny");
        }
        else
        {
            var events = await ReadAsync(session, Idle);
            events.ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
            (await answered.Task).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow");
        }
    }

    [Fact]
    public async Task a_level_change_applies_to_the_next_ask()
    {
        await using var agent = new FakeAcpAgent();
        var answers = new List<TaskCompletionSource<JsonElement>> { new(), new() };
        var turn = 0;
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                answers[turn++].SetResult(await agent.AskAsync("session/request_permission", Ask()));
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);

        await session.SendPromptAsync("one", null, CancellationToken.None);
        await ReadAsync(session, Idle);
        (await answers[0].Task).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow");

        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        await session.SendPromptAsync("two", null, CancellationToken.None);
        var asked = (await ReadAsync(session, seen => seen.Any(e => e.Type == EventTypes.PermissionAsked))).Last();
        await session.ReplyToPermissionAsync(PermissionEvents.ReadAsk(asked)!.Id, PermissionReplies.Once, null, CancellationToken.None);
        (await answers[1].Task.WaitAsync(TimeSpan.FromSeconds(5))).GetProperty("outcome").GetProperty("optionId").GetString().ShouldBe("allow");
    }

    [Fact]
    public async Task a_prompt_during_a_turn_steers_through_the_fleet_extension()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                await release.Task;
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.SendPromptAsync("first", null, CancellationToken.None);

        await Should.ThrowAsync<HarnessBusyException>(() => session.SendPromptAsync("queued", null, CancellationToken.None));
        await session.SendPromptAsync("steer me", new PromptOptions { Delivery = PromptDelivery.Steer }, CancellationToken.None);
        release.SetResult();

        agent.Received.Single(r => r.Method == "_fleet/steer").Params.GetProperty("prompt").GetString().ShouldBe("steer me");
    }

    [Fact]
    public async Task an_image_goes_to_the_agent_as_an_image_block_and_other_files_are_refused()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        await Should.ThrowAsync<NotSupportedException>(() => session.SendPromptAsync("see", new PromptOptions
        {
            Attachments = [new HarnessAttachment("application/pdf", "a.pdf", "AAAA")],
        }, CancellationToken.None));
        await session.SendPromptAsync("what is this?", new PromptOptions { Attachments = [new HarnessAttachment("image/png", "square.png", "iVBORw0=")] }, CancellationToken.None);

        for (var i = 0; i < 100 && !agent.Received.Any(r => r.Method == "session/prompt"); i++)
            await Task.Delay(20);
        var blocks = agent.Received.Single(r => r.Method == "session/prompt").Params.GetProperty("prompt");
        blocks[1].GetProperty("type").GetString().ShouldBe("image");
        blocks[1].GetProperty("mimeType").GetString().ShouldBe("image/png");
        blocks[1].GetProperty("data").GetString().ShouldBe("iVBORw0=");
    }

    [Fact]
    public async Task a_steer_the_agent_says_came_too_late_is_left_to_fleets_queue()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "_fleet/steer")
                throw new AcpException("No turn is running", AcpHarnessSession.NoTurnRunning);
            await release.Task;
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);
        await session.SendPromptAsync("first", null, CancellationToken.None);

        await Should.ThrowAsync<HarnessBusyException>(() => session.SendPromptAsync("late", new PromptOptions { Delivery = PromptDelivery.Steer }, CancellationToken.None));
        release.SetResult();

        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.TextContent).ShouldNotContain("late");
    }

    [Fact]
    public async Task a_refused_turn_is_a_readable_failure_not_a_hang()
    {
        await using var agent = new FakeAcpAgent { Handle = (_, _) => throw new InvalidOperationException("fusion routing transport failed") };
        await using var session = NewSession(agent);

        await session.SendPromptAsync("go", null, CancellationToken.None);
        var events = await ReadAsync(session, Idle);

        events.Single(e => e.Type == EventTypes.SessionError).Payload!.Value.GetProperty("error").GetProperty("message").GetString()
            .ShouldBe("fusion routing transport failed");
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
    }

    [Fact]
    public async Task the_agent_dying_mid_turn_fails_the_turn_with_what_it_said()
    {
        await using var agent = new FakeAcpAgent { Handle = (_, _) => new TaskCompletionSource<JsonNode?>().Task, Stderr = "signed out" };
        await using var session = NewSession(agent);
        await session.SendPromptAsync("go", null, CancellationToken.None);

        agent.Die();
        var events = await ReadAsync(session, Idle);

        events.Single(e => e.Type == EventTypes.SessionError).Payload!.Value.GetProperty("error").GetProperty("message").GetString()!.ShouldContain("signed out");
        session.Status.ShouldBe(HarnessSessionStatus.Error);
    }

    [Fact]
    public async Task loading_a_session_rebuilds_the_conversation_without_replaying_events()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) =>
        {
            if (method == "session/load")
            {
                agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "user_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Add a retry" } });
                agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Done." } });
            }

            return Task.FromResult<JsonNode?>(JsonNode.Parse("""{"models":{"currentModelId":"m1","availableModels":[{"modelId":"m1","name":"Model One"}]}}"""));
        };
        await using var session = NewSession(agent);

        await session.LoadAsync(null, CancellationToken.None);
        await Task.Delay(50);

        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.TextContent).ShouldBe(["Add a retry", "Done."]);
        session.Models.Single().Name.ShouldBe("Model One");
        using var none = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Should.ThrowAsync<OperationCanceledException>(async () => { await foreach (var _ in session.SubscribeAsync(none.Token)) { } });
    }

    [Fact]
    public async Task loading_a_session_passes_the_mcp_servers_and_ending_it_runs_OnEnded_once()
    {
        await using var agent = new FakeAcpAgent();
        string? sent = null;
        agent.Handle = (method, p) =>
        {
            if (method == "session/load")
                sent = p.GetProperty("mcpServers").GetRawText();
            return Task.FromResult<JsonNode?>(new JsonObject());
        };
        var ended = 0;
        var session = new AcpHarnessSession("copilot", "copilot-1", "fleet-1", "acp-1", "/work", agent, NullLogger.Instance) { OnEnded = () => ended++ };

        await session.LoadAsync(new JsonArray(new JsonObject { ["name"] = "fleet" }), CancellationToken.None);
        await session.DisposeAsync();
        await session.DisposeAsync();

        JsonNode.Parse(sent!)!.AsArray().Single()!["name"]!.GetValue<string>().ShouldBe("fleet");
        ended.ShouldBe(1);
    }

    private static readonly FleetToolSwitches BrowserOff = new(SessionMessages: false, Memory: false, WorkflowStep: false, Browser: false);

    /// <summary>A Fleet session on <paramref name="first"/> whose process listed <see cref="BrowserOff"/>; a restart starts <paramref name="next"/>.</summary>
    private static AcpHarnessSession Relaunchable(FakeAcpAgent first, Func<FleetToolSwitches?> now, Func<IAcpAgent> next) =>
        new("copilot", "copilot-1", "fleet-1", "acp-1", "/work", first, NullLogger.Instance)
        {
            McpServers = new JsonArray(new JsonObject { ["name"] = "fleet" }),
            FleetTools = BrowserOff,
            ReadFleetTools = () => Task.FromResult(now()),
            Relaunch = _ => Task.FromResult(next()),
        };

    private static Func<string, JsonElement, Task<JsonNode?>> Answers(FakeAcpAgent agent, string reply, string? replayed = null) => (method, _) =>
    {
        if (method == "session/load" && replayed is not null)
            agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = replayed } });
        if (method == "session/prompt")
            agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = reply } });
        return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
    };

    [Fact]
    public async Task a_tool_switch_changed_since_the_process_started_takes_a_new_one_that_loads_the_conversation()
    {
        await using var first = new FakeAcpAgent();
        first.Handle = Answers(first, "First.");
        await using var second = new FakeAcpAgent();
        second.Handle = Answers(second, "Second.", replayed: "First.");
        var switches = BrowserOff;
        await using var session = Relaunchable(first, () => switches, () => second);
        await session.SendPromptAsync("one", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        switches = BrowserOff with { Browser = true };
        await session.SendPromptAsync("two", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        first.IsRunning.ShouldBeFalse();
        var load = second.Received.Single(r => r.Method == "session/load").Params;
        load.GetProperty("sessionId").GetString().ShouldBe("acp-1");
        load.GetProperty("mcpServers")[0].GetProperty("name").GetString().ShouldBe("fleet");
        second.Received.Select(r => r.Method).ShouldBe(["session/load", "session/prompt"]);
        session.FleetTools.ShouldBe(switches);
        // What the new process replayed is the conversation the session already has: it isn't added twice.
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.TextContent).ShouldBe(["one", "First.", "two", "Second."]);
    }

    [Fact]
    public async Task a_new_process_gets_fleets_skills_again_and_the_agent_is_selected_again()
    {
        await using var first = new FakeAcpAgent();
        first.Handle = Answers(first, "First.");
        await using var second = new FakeAcpAgent();
        second.Handle = Answers(second, "Second.");
        var switches = BrowserOff;
        await using var session = Relaunchable(first, () => switches, () => second);
        session.SkillDirectory = "/data/copilot/skills/me";
        await session.SendPromptAsync("one", new PromptOptions { Agent = "reviewer" }, CancellationToken.None);
        await ReadAsync(session, Idle);

        switches = BrowserOff with { Browser = true };
        await session.SendPromptAsync("two", new PromptOptions { Agent = "reviewer" }, CancellationToken.None);
        await ReadAsync(session, Idle);

        var load = second.Received.Single(r => r.Method == "session/load").Params;
        load.GetProperty("_meta").GetProperty("skillDirectories")[0].GetString().ShouldBe("/data/copilot/skills/me");
        second.Received.Select(r => r.Method).ShouldBe(["session/load", "session/set_config_option", "session/prompt"]);
    }

    [Fact]
    public async Task the_same_switches_keep_the_process()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = Answers(agent, "Hi.");
        var relaunched = 0;
        await using var session = Relaunchable(agent, () => BrowserOff, () => { relaunched++; return new FakeAcpAgent(); });

        await session.SendPromptAsync("one", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        relaunched.ShouldBe(0);
        agent.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task a_new_process_that_cant_load_the_conversation_leaves_the_session_on_the_one_it_has()
    {
        await using var first = new FakeAcpAgent();
        first.Handle = Answers(first, "Still here.");
        await using var broken = new FakeAcpAgent();
        broken.Handle = (_, _) => throw new AcpException("Session not found", -32000);
        await using var session = Relaunchable(first, () => BrowserOff with { Browser = true }, () => broken);

        await session.SendPromptAsync("one", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        broken.IsRunning.ShouldBeFalse();
        first.IsRunning.ShouldBeTrue();
        first.Received.ShouldContain(r => r.Method == "session/prompt");
        session.FleetTools.ShouldBe(BrowserOff);
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.TextContent).ShouldBe(["one", "Still here."]);
    }

    [Fact]
    public async Task a_new_process_that_dies_while_loading_doesnt_end_the_session()
    {
        await using var first = new FakeAcpAgent();
        first.Handle = Answers(first, "Still here.");
        await using var dying = new FakeAcpAgent();
        dying.Handle = (_, _) =>
        {
            dying.Die();
            return new TaskCompletionSource<JsonNode?>().Task;
        };
        var ended = 0;
        await using var session = new AcpHarnessSession("copilot", "copilot-1", "fleet-1", "acp-1", "/work", first, NullLogger.Instance)
        {
            OnEnded = () => ended++,
            FleetTools = BrowserOff,
            ReadFleetTools = () => Task.FromResult<FleetToolSwitches?>(BrowserOff with { Browser = true }),
            Relaunch = _ => Task.FromResult<IAcpAgent>(dying),
        };

        await session.SendPromptAsync("one", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        // Its token still works: Fleet's tools answer the process the session kept.
        ended.ShouldBe(0);
        session.Status.ShouldBe(HarnessSessionStatus.Idle);
        first.Received.ShouldContain(r => r.Method == "session/prompt");
    }

    [Fact]
    public async Task a_usage_update_during_a_turn_reports_the_contexts_size_for_the_sessions_model()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) =>
        {
            if (method == "session/prompt")
                agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 4200, ["size"] = 128000 });
            return Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        };
        await using var session = NewSession(agent);
        session.ReadModels(JsonDocument.Parse("""{"models":{"currentModelId":"m1","availableModels":[{"modelId":"m1","name":"Model One"}]}}""").RootElement);

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        var events = await ReadAsync(session, Idle);

        var report = ContextEvents.ReadUsage(events.Single(e => e.Type == EventTypes.ContextUsage))!;
        (report.Call!.Used, report.Limit, report.ModelId).ShouldBe((4200, 128000, "m1"));
    }

    private static JsonNode? CommandsAnswer() => JsonNode.Parse("""
        {"commands":[
          {"name":"demo-skill","description":"A demo skill","hint":null,"kind":"skill"},
          {"name":"review","description":"Review","hint":"what","kind":"builtin"},
          {"name":"model","description":"Pick a model","hint":null,"kind":"builtin"},
          {"name":"ui-thing","description":"Client","hint":null,"kind":"client"}]}
        """);

    private static List<JsonElement> Sent(FakeAcpAgent agent, string method)
    {
        lock (agent.Received)
            return [.. agent.Received.Where(r => r.Method == method).Select(r => r.Params)];
    }

    private static async Task WaitForPromptsAsync(FakeAcpAgent agent, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Sent(agent, "session/prompt").Count < count)
            await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task commands_are_the_skills_and_the_allow_listed_built_ins_and_nothing_else()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult(CommandsAnswer());
        await using var session = NewSession(agent);

        var commands = await session.GetCommandsAsync(CancellationToken.None);

        commands.Select(c => (c.Name, c.Kind)).ShouldBe([("demo-skill", "skill"), ("review", "command")]);
        Sent(agent, "_fleet/commands").Single().GetProperty("sessionId").GetString().ShouldBe("acp-1");
    }

    [Fact]
    public async Task an_older_weave_copilot_offers_no_commands_and_only_Copilots_own_agent()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => throw new AcpException("Method not found", -32601);
        await using var session = NewSession(agent);

        (await session.GetCommandsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await session.GetAgentsAsync(CancellationToken.None)).Select(a => a.Name).ShouldBe(["copilot"]);
    }

    [Fact]
    public async Task a_command_is_sent_as_a_prompt_with_its_arguments_under_Fleets_message_id()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        var id = await session.SendCommandAsync(new CommandOptions { Command = "demo-skill", Arguments = "about\nrain", MessageId = "msg-1" }, CancellationToken.None);
        await WaitForPromptsAsync(agent, 1);

        id.ShouldBe("msg-1");
        Sent(agent, "session/prompt").Single().GetProperty("prompt")[0].GetProperty("text").GetString().ShouldBe("/demo-skill about rain");
    }

    [Fact]
    public async Task skills_ride_on_the_prompt_only_when_there_are_some()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        await session.SendPromptAsync("one", new PromptOptions { Skills = [] }, CancellationToken.None);
        await ReadAsync(session, Idle);
        await session.SendPromptAsync("two", new PromptOptions { Skills = ["demo-skill", "other"] }, CancellationToken.None);
        await WaitForPromptsAsync(agent, 2);

        var prompts = Sent(agent, "session/prompt");
        prompts[0].TryGetProperty("_meta", out _).ShouldBeFalse();
        prompts[1].GetProperty("_meta").GetProperty("skills").EnumerateArray().Select(e => e.GetString()).ShouldBe(["demo-skill", "other"]);
    }

    [Fact]
    public async Task a_loaded_session_gets_the_skills_folder()
    {
        await using var agent = new FakeAcpAgent();
        await using var session = NewSession(agent);

        await session.LoadAsync(null, CancellationToken.None, "/data/copilot/skills/me");
        await session.LoadAsync(null, CancellationToken.None);

        var loads = Sent(agent, "session/load");
        loads[0].GetProperty("_meta").GetProperty("skillDirectories").EnumerateArray().Select(e => e.GetString()).ShouldBe(["/data/copilot/skills/me"]);
        loads[1].TryGetProperty("_meta", out _).ShouldBeFalse();
        AcpHarnessSession.SkillDirectoriesMeta(null).ShouldBeNull();
    }

    [Fact]
    public async Task agents_are_Copilots_own_first_then_the_custom_ones()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult(JsonNode.Parse("""{"agents":[{"name":"pirate","displayName":"Pirate","description":"Talks like one","source":"project"}]}"""));
        await using var session = NewSession(agent);

        var agents = await session.GetAgentsAsync(CancellationToken.None);

        agents.Select(a => (a.Name, a.Mode)).ShouldBe([("copilot", "primary"), ("pirate", "primary")]);
        agents[1].Description.ShouldBe("Talks like one");
        Sent(agent, "_fleet/agents").Single().GetProperty("cwd").GetString().ShouldBe("/work");
    }

    [Fact]
    public async Task an_agent_is_selected_once_again_after_it_changes_and_copilot_deselects_with_an_empty_value()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        foreach (var name in new[] { "pirate", "pirate", "poet", "copilot", "copilot" })
        {
            await session.SendPromptAsync("hi", new PromptOptions { Agent = name }, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (session.Status == HarnessSessionStatus.Running)
                await Task.Delay(10, timeout.Token);
        }

        Sent(agent, "session/set_config_option").Select(p => (p.GetProperty("configId").GetString(), p.GetProperty("value").GetString()))
            .ShouldBe([("agent", "pirate"), ("agent", "poet"), ("agent", "")]);
    }

    [Fact]
    public async Task an_agent_that_cant_be_selected_fails_the_prompt_with_a_readable_error()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) => method == "session/set_config_option"
            ? throw new AcpException("Unknown agent: nope", -32000)
            : Task.FromResult<JsonNode?>(new JsonObject());
        await using var session = NewSession(agent);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => session.SendPromptAsync("hi", new PromptOptions { Agent = "nope" }, CancellationToken.None));

        error.Message.ShouldContain("nope");
        error.Message.ShouldContain("Unknown agent");
        Sent(agent, "session/prompt").ShouldBeEmpty();
    }

    private static JsonObject ShellNote(string phase, string? output = null, int? exit = null) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "_fleet/shell_command",
        ["params"] = new JsonObject
        {
            ["sessionId"] = "acp-1", ["toolCallId"] = "sh1", ["phase"] = phase, ["command"] = "git status", ["at"] = "2026-10-29T10:00:00.0000000+00:00",
            ["output"] = output, ["exitCode"] = exit,
        },
    };

    [Fact]
    public async Task a_shell_command_shows_under_the_id_fleet_gave_it_and_stays_in_the_history()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (method, _) =>
        {
            if (method == "_fleet/shell")
            {
                agent.Send(ShellNote("started"));
                agent.Send(ShellNote("ended", "clean", 0));
            }

            return Task.FromResult<JsonNode?>(new JsonObject { ["success"] = true, ["exitCode"] = 0 });
        };
        await using var session = NewSession(agent);

        await session.RunShellCommandAsync(new ShellCommandOptions { Command = "git status", MessageId = "msg_fleet" }, CancellationToken.None);
        var events = await ReadAsync(session, seen => seen.Count(e => e.Type is EventTypes.MessageCreated or EventTypes.MessageUpdated) == 2);

        agent.Received.Single(r => r.Method == "_fleet/shell").Params.GetProperty("command").GetString().ShouldBe("git status");
        events.First().Payload!.Value.GetProperty("info").GetProperty("id").GetString().ShouldBe("msg_fleet");
        var message = (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Single();
        (message.Id, message.Role).ShouldBe(("msg_fleet", ShellCommands.Role));
        message.Parts.OfType<ToolUsePart>().Single().State.ShouldBe(ToolUseState.Completed);
    }

    [Fact]
    public async Task a_shell_command_during_a_turn_is_refused_as_busy_by_either_side()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "session/prompt")
                await release.Task;
            if (method == "_fleet/shell")
                throw new AcpException("A turn is running; run the command when it's over", AcpHarnessSession.NoTurnRunning);
            return new JsonObject();
        };
        await using var session = NewSession(agent);

        // Fleet knows a turn is running.
        await session.SendPromptAsync("work", null, CancellationToken.None);
        await Should.ThrowAsync<HarnessBusyException>(() => session.RunShellCommandAsync(new ShellCommandOptions { Command = "ls" }, CancellationToken.None));
        agent.Received.ShouldNotContain(r => r.Method == "_fleet/shell");
        release.SetResult();
        await ReadAsync(session, Idle);

        // Copilot knows one is, and says so.
        await using var other = new FakeAcpAgent();
        other.Handle = (_, _) => throw new AcpException("A turn is running", AcpHarnessSession.NoTurnRunning);
        await using var second = NewSession(other);
        await Should.ThrowAsync<HarnessBusyException>(() => second.RunShellCommandAsync(new ShellCommandOptions { Command = "ls" }, CancellationToken.None));
    }

    [Fact]
    public async Task a_loaded_session_gives_its_messages_stable_ids_and_its_shell_commands_come_back()
    {
        async Task<string[]> LoadedIds()
        {
            await using var agent = new FakeAcpAgent();
            agent.Handle = (method, _) =>
            {
                if (method == "session/load")
                {
                    agent.Update("acp-1", new JsonObject
                    {
                        ["sessionUpdate"] = "user_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "hi" },
                        ["_meta"] = new JsonObject { ["fleetKey"] = new JsonObject { ["id"] = "u1", ["at"] = "2026-10-29T10:00:00.0000000+00:00" } },
                    });
                    agent.Update("acp-1", new JsonObject { ["sessionUpdate"] = "agent_message_chunk", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "hello" } });
                    agent.Send(ShellNote("started"));
                    agent.Send(ShellNote("ended", "clean", 0));
                }

                return Task.FromResult<JsonNode?>(new JsonObject());
            };
            await using var session = NewSession(agent);
            await session.LoadAsync(null, CancellationToken.None);
            await Task.Delay(100);
            return [.. (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Select(m => m.Role + ":" + m.Id)];
        }

        var first = await LoadedIds();

        first.Length.ShouldBe(3);
        first.ShouldBe(await LoadedIds());
        first[2].ShouldStartWith("shell:msg_");
    }

    [Fact]
    public async Task asking_off_the_record_sends_the_question_and_returns_the_answer_or_null()
    {
        await using var agent = new FakeAcpAgent();
        var answer = (string?)"It retries three times.";
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["answer"] = answer });
        await using var session = NewSession(agent);

        (await session.AskOffTheRecordAsync("what does it do?", CancellationToken.None)).ShouldBe("It retries three times.");
        answer = null;
        (await session.AskOffTheRecordAsync("and now?", CancellationToken.None)).ShouldBeNull();

        agent.Received[0].Method.ShouldBe("_fleet/ask");
        agent.Received[0].Params.GetProperty("question").GetString().ShouldBe("what does it do?");
        agent.Received[0].Params.GetProperty("sessionId").GetString().ShouldBe("acp-1");
    }

    [Fact]
    public async Task an_off_the_record_conversation_carries_the_earlier_questions_and_answers_into_each_follow_up()
    {
        await using var agent = new FakeAcpAgent();
        var n = 0;
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["answer"] = $"answer {++n}" });
        await using var session = NewSession(agent);

        await using var conversation = (await session.StartOffTheRecordAsync(CancellationToken.None))!;
        await conversation.AskAsync("first?", CancellationToken.None);
        var second = await conversation.AskAsync("second?", CancellationToken.None);

        second!.Text.ShouldBe("answer 2");
        var sent = agent.Received[1].Params.GetProperty("question").GetString()!;
        sent.ShouldContain("first?");
        sent.ShouldContain("answer 1");
        sent.ShouldEndWith("second?");
        agent.Received[0].Params.GetProperty("question").GetString().ShouldBe("first?");
    }

    [Fact]
    public async Task forking_names_a_boundary_after_everything_keyed_by_the_last_copied_message()
    {
        const string at = "2026-10-29T10:00:00.0000000+00:00";
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["sessionId"] = "acp-fork",
            ["boundary"] = new JsonObject { ["id"] = "u1", ["at"] = at, ["offset"] = 1 },
        });
        await using var session = NewSession(agent);

        var fork = await session.ForkConversationAsync(CancellationToken.None);

        agent.Received.Single().Method.ShouldBe("_fleet/fork");
        fork!.ResumeToken.ShouldBe("acp-fork");
        fork.BoundaryMessageId.ShouldBe(AscendingMessageId.Ceiling(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public async Task forking_a_session_with_nothing_to_copy_has_no_boundary()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["sessionId"] = "acp-fork", ["boundary"] = null });
        await using var session = NewSession(agent);

        var fork = await session.ForkConversationAsync(CancellationToken.None);

        (fork!.ResumeToken, fork.BoundaryMessageId).ShouldBe(("acp-fork", null));
    }

    private static JsonObject Ask() => new()
    {
        ["sessionId"] = "acp-1",
        ["toolCall"] = new JsonObject { ["toolCallId"] = "c1", ["title"] = "Write notes.md", ["kind"] = "edit" },
        ["options"] = new JsonArray(
            new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
            new JsonObject { ["optionId"] = "always", ["name"] = "Always", ["kind"] = "allow_always" },
            new JsonObject { ["optionId"] = "deny", ["name"] = "Deny", ["kind"] = "reject_once" }),
    };

    [Fact]
    public async Task model_notes_go_in_the_prompts_meta_and_not_in_fleets_conversation()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        await session.SendPromptAsync("hello", new PromptOptions { ModelNotes = ["Note one", "Note two"] }, CancellationToken.None);
        await ReadAsync(session, Idle);

        var sent = agent.Received.Single(r => r.Method == "session/prompt").Params;
        sent.GetProperty("_meta").GetProperty("modelNotes").EnumerateArray().Select(n => n.GetString()).ShouldBe(["Note one", "Note two"]);
        sent.GetProperty("prompt")[0].GetProperty("text").GetString().ShouldBe("hello");
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Single(m => m.Role == "user").TextContent.ShouldBe("hello");
    }

    [Fact]
    public async Task a_prompt_without_notes_has_no_meta()
    {
        await using var agent = new FakeAcpAgent();
        agent.Handle = (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["stopReason"] = "end_turn" });
        await using var session = NewSession(agent);

        await session.SendPromptAsync("hello", null, CancellationToken.None);
        await ReadAsync(session, Idle);

        agent.Received.Single(r => r.Method == "session/prompt").Params.TryGetProperty("_meta", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task compacting_shows_the_session_busy_then_idle_and_holds_a_prompt_meanwhile()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "_fleet/compact")
            {
                agent.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "_fleet/compaction", ["params"] = new JsonObject { ["sessionId"] = "acp-1", ["phase"] = "started", ["trigger"] = "manual" } });
                await release.Task;
                agent.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "_fleet/compaction", ["params"] = new JsonObject { ["sessionId"] = "acp-1", ["phase"] = "ended", ["trigger"] = "manual", ["summary"] = "Sum" } });
                return new JsonObject { ["success"] = true };
            }

            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);

        var compacting = session.CompactAsync(new CompactOptions(), CancellationToken.None);
        await Should.ThrowAsync<HarnessBusyException>(() => session.SendPromptAsync("too soon", null, CancellationToken.None));
        release.SetResult();
        await compacting;
        var events = await ReadAsync(session, Idle);

        var statuses = events.Where(e => e.Type == EventTypes.SessionStatus).Select(e => e.Payload!.Value.GetProperty("status").GetProperty("type").GetString());
        statuses.ShouldBe(["busy", "idle"]);
        events.ShouldContain(e => e.Type == EventTypes.ContextCompaction);
        (await session.GetMessagesAsync(null, CancellationToken.None)).Messages.Single().Parts.Single().ShouldBeOfType<CompactionPart>().Summary.ShouldBe("Sum");
        await session.SendPromptAsync("now", null, CancellationToken.None);
    }

    [Fact]
    public async Task compacting_while_a_turn_runs_is_busy_whether_fleet_knows_or_the_agent_says()
    {
        await using var agent = new FakeAcpAgent();
        var release = new TaskCompletionSource();
        agent.Handle = async (method, _) =>
        {
            if (method == "_fleet/compact")
                throw new AcpException("A turn is running", AcpHarnessSession.NoTurnRunning);
            await release.Task;
            return new JsonObject { ["stopReason"] = "end_turn" };
        };
        await using var session = NewSession(agent);

        await Should.ThrowAsync<HarnessBusyException>(() => session.CompactAsync(new CompactOptions(), CancellationToken.None));
        await session.SendPromptAsync("first", null, CancellationToken.None);
        await Should.ThrowAsync<HarnessBusyException>(() => session.CompactAsync(new CompactOptions(), CancellationToken.None));
        agent.Received.Count(r => r.Method == "_fleet/compact").ShouldBe(1);
        release.SetResult();
    }
}
