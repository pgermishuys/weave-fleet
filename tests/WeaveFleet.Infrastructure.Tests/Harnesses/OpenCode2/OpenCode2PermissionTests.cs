using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode2;

namespace WeaveFleet.Infrastructure.Tests.Harnesses.OpenCode2;

/// <summary>
/// An OpenCode 2 session's asks: decided for the session's permission level, put to the user as Fleet's
/// <c>permission.asked</c> when the level doesn't allow them, and answered on V2's reply endpoint.
/// </summary>
public sealed class OpenCode2PermissionTests
{
    private const string Session = "ses_permission_session";

    [Fact]
    public async Task An_ask_the_level_allows_is_answered_once_and_the_user_never_sees_it()
    {
        var (api, server, session) = Start();
        await using var _ = server;
        await using var __ = session;

        session.OnEvent(Asked("per_1", "shell", "ls"));

        await WaitForAsync(() => api.Requests.Any(r => r.Path.EndsWith("/permission/per_1/reply", StringComparison.Ordinal)));
        Decision(api, "per_1").ShouldBe("once");
        (await ReadAvailableAsync(session)).ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
    }

    [Fact]
    public async Task An_ask_the_level_doesnt_allow_waits_for_the_user_and_the_session_needs_them()
    {
        var (api, server, session) = Start();
        await using var _ = server;
        await using var __ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);

        session.OnEvent(Asked("per_1", "shell", "git push origin main", command: "git push origin main"));

        var events = await ReadAvailableAsync(session);
        var asked = events.Where(e => e.Type == EventTypes.PermissionAsked).ShouldHaveSingleItem().Payload!.Value;
        asked.GetProperty("id").GetString().ShouldBe("per_1");
        asked.GetProperty("sessionId").GetString().ShouldBe("fleet-1");
        asked.GetProperty("kind").GetString().ShouldBe(PermissionKinds.Shell);
        asked.GetProperty("title").GetString().ShouldBe("git push origin main");
        asked.GetProperty("always").EnumerateArray().Select(a => a.GetString()).ShouldBe(["git push *"]);
        asked.GetProperty("callId").GetString().ShouldBe("call_1");
        StatusesIn(events).ShouldBe([ActivityStatuses.WaitingInput]);
        api.Requests.ShouldNotContain(r => r.Path.Contains("/permission/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dont_ask_again_answers_this_ask_once_and_the_next_matching_one_without_the_user()
    {
        var (api, server, session) = Start();
        await using var _ = server;
        await using var __ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        session.OnEvent(Asked("per_1", "shell", "git push origin main"));
        await ReadAvailableAsync(session);

        await session.ReplyToPermissionAsync("per_1", PermissionReplies.Always, null, CancellationToken.None);

        // V2's own "always" would outlive the session; Fleet keeps the rule and tells V2 "once".
        Decision(api, "per_1").ShouldBe("once");
        var answered = await ReadAvailableAsync(session);
        answered.ShouldContain(e => e.Type == EventTypes.PermissionReplied);
        StatusesIn(answered).ShouldBe([ActivityStatuses.Busy]);

        session.OnEvent(Asked("per_2", "shell", "git push origin other"));
        await WaitForAsync(() => api.Requests.Any(r => r.Path.EndsWith("/permission/per_2/reply", StringComparison.Ordinal)));
        (await ReadAvailableAsync(session)).ShouldNotContain(e => e.Type == EventTypes.PermissionAsked);
    }

    [Fact]
    public async Task A_refusal_carries_the_users_words_to_the_agent()
    {
        var (api, server, session) = Start();
        await using var _ = server;
        await using var __ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        session.OnEvent(Asked("per_1", "shell", "rm -rf dist"));

        await session.ReplyToPermissionAsync("per_1", PermissionReplies.Reject, "Leave dist alone", CancellationToken.None);

        var body = JsonDocument.Parse(api.Requests.Last(r => r.Path.EndsWith("/permission/per_1/reply", StringComparison.Ordinal)).Body!).RootElement;
        body.GetProperty("decision").GetString().ShouldBe("reject");
        body.GetProperty("message").GetString().ShouldBe("Leave dist alone");
    }

    [Fact]
    public async Task An_answer_to_an_ask_that_no_longer_waits_says_so()
    {
        var (_, server, session) = Start();
        await using var _ = server;
        await using var __ = session;

        await Should.ThrowAsync<KeyNotFoundException>(
            () => session.ReplyToPermissionAsync("per_missing", PermissionReplies.Once, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_turn_that_ends_takes_its_asks_with_it()
    {
        var (_, server, session) = Start();
        await using var _ = server;
        await using var __ = session;
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Ask), CancellationToken.None);
        session.OnEvent(Asked("per_1", "shell", "sleep 100"));
        await ReadAvailableAsync(session);

        session.OnEvent(Event("session.execution.interrupted", $$"""{"sessionID":"{{Session}}"}"""));

        var replied = (await ReadAvailableAsync(session)).Where(e => e.Type == EventTypes.PermissionReplied).ShouldHaveSingleItem().Payload!.Value;
        replied.GetProperty("reply").GetString().ShouldBe(PermissionReplies.Gone);
        await Should.ThrowAsync<KeyNotFoundException>(
            () => session.ReplyToPermissionAsync("per_1", PermissionReplies.Once, null, CancellationToken.None));
    }

    [Fact]
    public async Task The_sessions_rules_follow_the_level_and_keep_the_step_tool_hidden()
    {
        var (api, server, session) = Start(hidesStepTool: true);
        await using var _ = server;
        await using var __ = session;

        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Edits), CancellationToken.None);
        await session.ApplyPermissionsAsync(new PermissionPolicy(PermissionLevels.Edits), CancellationToken.None);

        // Set once: the same level again changes nothing.
        var patch = api.Requests.Where(r => r.Method == HttpMethod.Patch).ShouldHaveSingleItem();
        var rules = JsonDocument.Parse(patch.Body!).RootElement.GetProperty("permissions").EnumerateArray()
            .Select(rule => (rule.GetProperty("action").GetString(), rule.GetProperty("effect").GetString())).ToList();
        rules[0].ShouldBe(("*", "ask"));
        rules.ShouldContain(("read", "allow"));
        rules.ShouldContain(("edit", "allow"));
        rules.ShouldNotContain(("shell", "allow"));
        rules[^2].ShouldBe(("browser", "deny"));
        rules[^1].ShouldBe((WeaveFleet.Application.Workflows.FleetWorkflows.StepTool, "deny"));
    }

    [Theory]
    [InlineData(PermissionLevels.All)]
    [InlineData(PermissionLevels.Ask)]
    [InlineData(PermissionLevels.Edits)]
    public void Every_level_keeps_the_browser_tools_hidden(string level)
    {
        // V2 applies the last rule that matches, so the deny must follow the allow or ask for everything.
        var rules = OpenCode2HttpClient.RulesFor(level, hideStepTool: false);
        rules[^1].ShouldBe(OpenCode2HttpClient.DenyBrowser);
        rules[0].Action.ShouldBe("*");
    }

    [Theory]
    [InlineData(PermissionLevels.All, false)]
    [InlineData(PermissionLevels.Ask, true)]
    [InlineData(PermissionLevels.Edits, true)]
    public void Only_a_level_that_asks_asks_v2_for_anything(string level, bool asks)
        => OpenCode2HttpClient.RulesFor(level, hideStepTool: false)
            .Any(rule => rule is { Action: "*", Effect: "ask" }).ShouldBe(asks);

    private static (StubHandler Api, OpenCode2Server Server, OpenCode2HarnessSession Session) Start(bool hidesStepTool = false)
    {
        var permissions = hidesStepTool
            ? $$"""[{"action":"*","resource":"*","effect":"allow"},{"action":"{{WeaveFleet.Application.Workflows.FleetWorkflows.StepTool}}","resource":"*","effect":"deny"}]"""
            : """[{"action":"*","resource":"*","effect":"allow"}]""";
        var api = new StubHandler(request => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == $"/api/session/{Session}"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$$"""{"data":{"id":"{{{Session}}}","permissions":{{{permissions}}}}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var server = new OpenCode2Server("local-user", OpenCode2Fixtures.ClientServing("", api), "token", process: null, NullLogger.Instance);
        var session = new OpenCode2HarnessSession(
            "opencode2-1",
            new OpenCode2SessionInfo { Id = Session },
            new OpenCode2SessionContext("fleet-1", "local-user", "/work", null, null),
            server,
            _ => Task.FromResult(server),
            analytics: null,
            NullLogger.Instance);
        return (api, server, session);
    }

    private static OpenCode2Event Asked(string id, string action, string resource, string? command = null)
        => Event("permission.asked", JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sessionID"] = Session,
            ["action"] = action,
            ["resources"] = new[] { resource },
            ["save"] = new[] { string.Join(' ', resource.Split(' ').Take(2)) + " *" },
            ["metadata"] = command is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["command"] = command },
            ["source"] = new Dictionary<string, object?> { ["type"] = "tool", ["messageID"] = "msg_1", ["id"] = "call_1" },
        }));

    private static string? Decision(StubHandler api, string id)
        => JsonDocument.Parse(api.Requests.Last(r => r.Path.EndsWith($"/permission/{id}/reply", StringComparison.Ordinal)).Body!)
            .RootElement.GetProperty("decision").GetString();

    private static List<string?> StatusesIn(IEnumerable<HarnessEvent> events)
        => [.. events.Where(e => e.Type == EventTypes.SessionStatus)
            .Select(e => e.Payload!.Value.GetProperty("status").GetProperty("type").GetString())];

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }
    }

    private static async Task<List<HarnessEvent>> ReadAvailableAsync(OpenCode2HarnessSession session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var events = new List<HarnessEvent>();
        try
        {
            await foreach (var evt in session.SubscribeAsync(timeout.Token))
                events.Add(evt);
        }
        catch (OperationCanceledException)
        {
            // Read everything written so far.
        }

        return events;
    }

    private static OpenCode2Event Event(string type, string data) => new()
    {
        Id = $"evt_{Guid.NewGuid():N}",
        Created = 1_789_764_124_966,
        Type = type,
        Data = JsonDocument.Parse(data).RootElement.Clone(),
    };
}
