extern alias FakeLlm;

using System.Collections.Concurrent;
using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// One session's agent messages another, with a real <c>opencode</c> and a scripted model. Both sessions run on
/// one pooled process with a scratch HOME, so the receiver never starts from the real user's OpenCode config.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionMessagesLiveTests
{
    private const string Owner = "local-user";
    private const string Sender = "msg-sender";
    private const string Receiver = "msg-receiver";
    private const string SenderPrompt = "Tell the docs session that the login redirect changed.";
    private const string Note = "The login redirect changed. Please update the authentication docs.";
    private const string Reply = "Docs updated: the redirect section now points at /auth/callback.";
    private const string SlowPrompt = "Run the slow link check.";
    private const string SlowReply = "The link check passed.";
    private const string FollowUp = "Thanks. Now update the changelog too.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task With_messages_off_an_agent_that_prompts_another_session_through_the_API_speaks_as_the_user()
    {
        await RunAsync(
            messagesOn: false,
            senderCall: CurlPrompt(),
            expectDelivery: true,
            assert: (requests, _) =>
            {
                // No tool, and the API takes it.
                OfferedToolNames(requests).ShouldNotContain("fleet_message");
                LastToolResult(requests, SenderPrompt).ShouldContain("HTTP 200");

                // The receiver's model got the text as a plain user message: nothing says another session sent it.
                LastUserText(requests.Single(r => LastUserText(r) is { } text && text.Contains(Note))).ShouldBe(Note);
            });
    }

    [OpenCodeFact]
    public async Task With_messages_on_the_API_refuses_an_agents_prompt_and_names_the_tool()
    {
        await RunAsync(
            messagesOn: true,
            senderCall: CurlPrompt(),
            expectDelivery: false,
            assert: (requests, _) =>
            {
                var result = LastToolResult(requests, SenderPrompt);
                result.ShouldContain("HTTP 409");
                result.ShouldContain("Use the fleet_message tool to message a session.");
                requests.ShouldNotContain(r => LastUserText(r) != null && LastUserText(r)!.Contains(Note));
            });
    }

    [OpenCodeFact]
    public async Task With_messages_on_an_agent_messages_another_session_and_it_arrives_from_that_session()
    {
        await RunAsync(
            messagesOn: true,
            senderCall: new ScriptedToolCall("call_message", "fleet_message", JsonSerializer.Serialize(new { sessionId = Receiver, text = Note })),
            expectDelivery: true,
            assert: (requests, events) =>
            {
                OfferedToolNames(requests).ShouldContain("fleet_message");
                LastToolResult(requests, SenderPrompt).ShouldContain("Delivered to Update documentation (msg-receiver).");

                // The receiver's model got it wrapped with the sender Fleet resolved from the calling process.
                LastUserText(requests.Single(r => LastUserText(r) is { } text && text.Contains(Note))).ShouldBe(
                    $"<fleet-session-message from=\"{Sender}\" title=\"Fix authentication flow\">\n{Note}\n</fleet-session-message>");

                var messaged = events.Where(e => e.Type == "session.messaged").ShouldHaveSingleItem();
                messaged.Topic.ShouldBe($"session:{Receiver}");
                messaged.Payload.GetProperty("fromSessionId").GetString().ShouldBe(Sender);
                messaged.Payload.GetProperty("toSessionId").GetString().ShouldBe(Receiver);
            });
    }

    [OpenCodeFact]
    public async Task A_session_that_asks_is_woken_with_the_other_sessions_reply()
    {
        await RunAsync(
            new Scenario
            {
                SenderCall = MessageCall(notifyWhenDone: true),
                Route = request => LastUserText(request) is { } text && text.StartsWith("<fleet-session-update", StringComparison.Ordinal)
                    ? new ScriptedLlmResponse { Text = "Thanks." }
                    : null,
                Done = requests => requests.Any(r => IsUpdate(LastUserText(r))),
                Assert = (requests, events) =>
                {
                    LastToolResult(requests, SenderPrompt).ShouldContain("Fleet will send you its reply when it's done");

                    // One update, after the sender's own turn ended, carrying the receiver's reply.
                    requests.Where(r => IsUpdate(LastUserText(r))).ShouldHaveSingleItem();
                    LastUserText(requests.Single(r => IsUpdate(LastUserText(r)))).ShouldBe(
                        $"<fleet-session-update session=\"{Receiver}\" title=\"Update documentation\" outcome=\"finished\">\n{Reply}\n</fleet-session-update>");

                    var reported = events.Where(e => e.Type == "session.reported").ShouldHaveSingleItem();
                    reported.Topic.ShouldBe($"session:{Sender}");
                    reported.Payload.GetProperty("fromSessionId").GetString().ShouldBe(Receiver);
                    reported.Payload.GetProperty("toSessionId").GetString().ShouldBe(Sender);
                    reported.Payload.GetProperty("outcome").GetString().ShouldBe("finished");
                },
            });
    }

    [OpenCodeFact]
    public async Task A_turn_the_other_session_was_already_running_does_not_count()
    {
        await RunAsync(
            new Scenario
            {
                SenderCall = MessageCall(notifyWhenDone: true),
                ReceiverBusyFirst = true,
                Route = request => LastUserText(request) is { } text && text.StartsWith("<fleet-session-update", StringComparison.Ordinal)
                    ? new ScriptedLlmResponse { Text = "Thanks." }
                    : null,
                Done = requests => requests.Any(r => IsUpdate(LastUserText(r))),
                Assert = (requests, _) =>
                {
                    // The receiver was on its slow check when the message arrived. OpenCode hands a message that
                    // arrives mid-turn to the model at the turn's next step, after the check's result, and the turn
                    // goes on from there. So the receiver's turn ends once, having handled both.
                    var list = requests.ToList();
                    var noteRead = list.FindIndex(r => LastUserText(r) is { } text && text.Contains(Note));
                    FirstUserText(list[noteRead]).ShouldBe(SlowPrompt);
                    list[noteRead].ShouldContain("call_slow");

                    // Told once, after the receiver answered the message.
                    var told = list.FindIndex(r => IsUpdate(LastUserText(r)));
                    told.ShouldBeGreaterThan(noteRead);
                    list.Where(r => IsUpdate(LastUserText(r))).ShouldHaveSingleItem();

                    // The update is about the turn that handled the message, not the check.
                    var update = LastUserText(requests.Single(r => IsUpdate(LastUserText(r))))!;
                    update.ShouldContain(Reply);
                    update.ShouldNotContain(SlowReply);
                },
            });
    }

    [OpenCodeFact]
    public async Task A_turn_an_update_started_cannot_ask_to_be_told_again()
    {
        await RunAsync(
            new Scenario
            {
                SenderCall = MessageCall(notifyWhenDone: true),
                // Woken by the update, the sender tries to keep the loop going.
                Route = request => LastUserText(request) is { } text && text.StartsWith("<fleet-session-update", StringComparison.Ordinal)
                    ? new ScriptedLlmResponse
                    {
                        StopReason = "tool_calls",
                        ToolCalls = [new ScriptedToolCall("call_again", "fleet_message", JsonSerializer.Serialize(new { sessionId = Receiver, text = FollowUp, notifyWhenDone = true }))],
                    }
                    : null,
                Done = requests => requests.Any(r => LastToolText(r) is { } text && text.Contains("can't ask to be told again")),
                Assert = (requests, events) =>
                {
                    requests.Single(r => LastToolText(r) is { } text && text.Contains("can't ask to be told again"))
                        .ShouldContain("Send it with notifyWhenDone false.");

                    // Refused outright: the receiver never got the follow-up, and nothing more was sent either way.
                    requests.ShouldNotContain(r => LastUserText(r) != null && LastUserText(r)!.Contains(FollowUp));
                    events.Where(e => e.Type == "session.messaged").ShouldHaveSingleItem();
                    events.Where(e => e.Type == "session.reported").ShouldHaveSingleItem();
                },
            });
    }

    private static bool IsUpdate(string? text) => text is not null && text.StartsWith("<fleet-session-update", StringComparison.Ordinal);

    private static ScriptedToolCall MessageCall(bool notifyWhenDone)
        => new("call_message", "fleet_message", JsonSerializer.Serialize(new { sessionId = Receiver, text = Note, notifyWhenDone }));

    // Long enough for the sender's message to arrive while it runs.
    private static ScriptedToolCall SlowCheck()
        => new("call_slow", "bash", JsonSerializer.Serialize(new { command = "sleep 6", description = "Check the links" }));

    private static ScriptedToolCall CurlPrompt()
    {
        var curl = $$"""curl -s -w '\nHTTP %{http_code}' -X POST "$FLEET_URL/api/sessions/{{Receiver}}/prompt" -H 'content-type: application/json' -d '{"text":"{{Note}}"}'""";
        return new ScriptedToolCall("call_curl", "bash", JsonSerializer.Serialize(new { command = curl, description = "Message the docs session" }));
    }

    private static Task RunAsync(
        bool messagesOn,
        ScriptedToolCall senderCall,
        bool expectDelivery,
        Action<IReadOnlyList<string>, IReadOnlyList<BroadcastEvent>> assert)
        => RunAsync(new Scenario
        {
            MessagesOn = messagesOn,
            SenderCall = senderCall,
            Done = requests => requests.Any(r => LastRole(r) == "tool" && FirstUserText(r) == SenderPrompt)
                && (!expectDelivery || requests.Any(r => LastUserText(r) is { } text && text.Contains(Note))),
            Assert = assert,
        });

    private sealed record Scenario
    {
        public bool MessagesOn { get; init; } = true;
        public required ScriptedToolCall SenderCall { get; init; }

        /// <summary>Answers checked before the usual ones; null falls through to them.</summary>
        public Func<string, ScriptedLlmResponse?>? Route { get; init; }

        /// <summary>The receiver starts a slow turn of its own before the sender messages it.</summary>
        public bool ReceiverBusyFirst { get; init; }

        /// <summary>Done when the model has seen these requests (only turns: they offer tools).</summary>
        public required Func<IReadOnlyList<string>, bool> Done { get; init; }

        public required Action<IReadOnlyList<string>, IReadOnlyList<BroadcastEvent>> Assert { get; init; }
    }

    private static async Task RunAsync(Scenario scenario)
    {
        var messagesOn = scenario.MessagesOn;
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-messages-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var plugin = Path.Combine(root, "no-op.ts");
        File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, plugin);

        // Two sessions share one model, so it answers by what it's asked, not by arrival order.
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Messages" };
        for (var i = 0; i < 30; i++)
        {
            llm.Queue.Enqueue(request =>
            {
                if (scenario.Route?.Invoke(request) is { } routed)
                    return routed;
                if (LastRole(request) == "tool")
                    return new ScriptedLlmResponse { Text = FirstUserText(request) == SlowPrompt ? SlowReply : "Done." };
                if (LastUserText(request) == SenderPrompt)
                    return new ScriptedLlmResponse { StopReason = "tool_calls", ToolCalls = [scenario.SenderCall] };
                if (LastUserText(request) == SlowPrompt)
                    return new ScriptedLlmResponse { StopReason = "tool_calls", ToolCalls = [SlowCheck()] };
                if (LastUserText(request) is { } text && text.Contains(Note))
                    return new ScriptedLlmResponse { Text = Reply };
                return new ScriptedLlmResponse { Text = "On it." };
            });
        }

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            SeedSessions(services, workspace);

            var events = new ConcurrentQueue<BroadcastEvent>();
            var collecting = CollectAsync(services.GetRequiredService<IEventBroadcaster>(), events, ct);

            // The owner's switch in Settings, then the environment the runtime prepares from it.
            var runtime = services.GetRequiredService<OpenCodeHarnessRuntime>();
            if (messagesOn)
            {
                using (BackgroundUserContext.BeginScope(Owner))
                using (var scope = services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(SessionMessages.PreferenceKey, "true");
            }

            var prepared = await runtime.PrepareRuntimeAsync(
                new RuntimePreparationContext { UserId = Owner, UserCredentials = [], WorkingDirectory = workspace },
                ct);
            foreach (var (name, value) in ((OpenCodeLaunchArtifacts)prepared.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).EnvironmentVariables)
                processEnvironment[name] = value;
            processEnvironment.ContainsKey(SessionMessages.EnvironmentVariable).ShouldBe(messagesOn);

            await using var sender = await SpawnAsync(runtime, Sender, workspace, processEnvironment, ct);
            await using var receiver = await SpawnAsync(runtime, Receiver, workspace, processEnvironment, ct);
            RegisterLikeTheOrchestrator(services, Sender, sender);
            RegisterLikeTheOrchestrator(services, Receiver, receiver);
            await sender.WaitForEventSubscriptionAsync(ct);
            await receiver.WaitForEventSubscriptionAsync(ct);

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));

                // Through the orchestrator, as the user would, so Fleet sees the receiver's turn start.
                if (scenario.ReceiverBusyFirst)
                {
                    using (BackgroundUserContext.BeginScope(Owner))
                    using (var scope = services.CreateScope())
                        (await scope.ServiceProvider.GetRequiredService<SessionOrchestrator>().PromptSessionAsync(Receiver, SlowPrompt, ct: ct)).IsSuccess.ShouldBeTrue();
                    await WaitForAsync(
                        () => llm.Queue.Requests.Where(OffersTools).ToList(),
                        requests => requests.Any(r => LastUserText(r) == SlowPrompt),
                        wait.Token);
                    await Task.Delay(500, ct);
                }

                await sender.SendPromptAsync(SenderPrompt, null, ct);

                await WaitForAsync(() => llm.Queue.Requests.Where(OffersTools).ToList(), scenario.Done, wait.Token);

                // Long enough for anything that shouldn't happen to show up.
                await Task.Delay(2000, ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "Timed out. LLM requests:\n" + string.Join("\n", llm.Queue.Requests.Select(r => $"first={FirstUserText(r)} last={LastRole(r)}:{Trim(LastUserText(r) ?? LastToolText(r))}")));
            }

            // Only turns count: OpenCode's title requests carry the same text but offer no tools.
            scenario.Assert([.. llm.Queue.Requests.Where(OffersTools)], [.. events]);

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task CollectAsync(IEventBroadcaster broadcaster, ConcurrentQueue<BroadcastEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var e in broadcaster.SubscribeAsync([$"session:{Sender}", $"session:{Receiver}"], Owner, ct))
                events.Enqueue(e);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static HashSet<string> OfferedToolNames(IEnumerable<string> requests)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            using var body = JsonDocument.Parse(request);
            if (!body.RootElement.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("function", out var function) && function.TryGetProperty("name", out var name))
                    names.Add(name.GetString()!);
            }
        }

        return names;
    }

    private static async Task<IHarnessSession> SpawnAsync(
        OpenCodeHarnessRuntime runtime, string sessionId, string workspace, Dictionary<string, string> environment, CancellationToken ct)
        => await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = sessionId,
                WorkingDirectory = workspace,
                OwnerUserId = Owner,
                LaunchArtifacts = new OpenCodeLaunchArtifacts(environment),
            },
            ct);

    private static void SeedSessions(IServiceProvider services, string workspace)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-live', '{workspace}', 'Live', '2026-09-18T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-live', 0, NULL, '{workspace}', '', 'running', '2026-09-18T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{Sender}', 'ws-live', 'inst-live', 'pending', 'Fix authentication flow', 'active', '{workspace}',
                    'running', 'active', '2026-09-18T00:00:00+00:00', '{Owner}'),
                   ('{Receiver}', 'ws-live', 'inst-live', 'pending', 'Update documentation', 'active', '{workspace}',
                    'running', 'active', '2026-09-18T00:00:00+00:00', '{Owner}');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Points a seeded session at its spawned instance and registers it, as the orchestrator does.</summary>
    private static void RegisterLikeTheOrchestrator(IServiceProvider services, string sessionId, IHarnessSession session)
    {
        using (var scope = services.CreateScope())
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        using (var command = connection.CreateCommand())
        {
            var token = session.ResumeToken is null ? "NULL" : $"'{session.ResumeToken}'";
            command.CommandText = $"""
                INSERT OR IGNORE INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('{session.InstanceId}', 0, NULL, '', '', 'running', '2026-09-18T00:00:00+00:00', '{Owner}');
                UPDATE sessions SET instance_id = '{session.InstanceId}', harness_resume_token = {token} WHERE id = '{sessionId}';
                """;
            command.ExecuteNonQuery();
        }

        services.GetRequiredService<InstanceTracker>().Register(session.InstanceId, session);
    }

    private static bool OffersTools(string request)
    {
        using var body = JsonDocument.Parse(request);
        return body.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0;
    }

    private static string? LastRole(string request)
    {
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("role").GetString();
    }

    private static string? FirstUserText(string request) => UserTexts(request).FirstOrDefault();

    private static string? LastUserText(string request)
        => LastRole(request) == "user" ? UserTexts(request).LastOrDefault() : null;

    private static List<string> UserTexts(string request)
    {
        using var body = JsonDocument.Parse(request);
        return body.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "user")
            .Select(m => ContentText(m.GetProperty("content")))
            .ToList();
    }

    private static string? LastToolText(string request)
    {
        using var body = JsonDocument.Parse(request);
        var messages = body.RootElement.GetProperty("messages");
        var last = messages[messages.GetArrayLength() - 1];
        return last.GetProperty("role").GetString() == "tool" ? ContentText(last.GetProperty("content")) : null;
    }

    private static string LastToolResult(IReadOnlyList<string> requests, string firstUserText)
        => requests.Where(r => LastRole(r) == "tool" && FirstUserText(r) == firstUserText).Select(r => LastToolText(r)!).First();

    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString()!,
        JsonValueKind.Array => string.Concat(content.EnumerateArray()
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null)),
        _ => string.Empty,
    };

    private static string Trim(string? value) => value is null ? "-" : value[..Math.Min(value.Length, 200)];

    private static async Task<T> WaitForAsync<T>(Func<T> read, Func<T, bool> done, CancellationToken ct)
    {
        while (true)
        {
            var value = read();
            if (done(value))
                return value;

            try
            {
                await Task.Delay(200, ct);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }
    }
}
