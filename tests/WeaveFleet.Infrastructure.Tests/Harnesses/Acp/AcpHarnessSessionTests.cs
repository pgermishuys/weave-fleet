using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Analytics;
using WeaveFleet.Domain.Harnesses;
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

    private static JsonObject Ask() => new()
    {
        ["sessionId"] = "acp-1",
        ["toolCall"] = new JsonObject { ["toolCallId"] = "c1", ["title"] = "Write notes.md", ["kind"] = "edit" },
        ["options"] = new JsonArray(
            new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
            new JsonObject { ["optionId"] = "always", ["name"] = "Always", ["kind"] = "allow_always" },
            new JsonObject { ["optionId"] = "deny", ["name"] = "Deny", ["kind"] = "reject_once" }),
    };
}
