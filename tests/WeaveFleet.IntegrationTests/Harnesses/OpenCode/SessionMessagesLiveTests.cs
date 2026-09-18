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

    private static ScriptedToolCall CurlPrompt()
    {
        var curl = $$"""curl -s -w '\nHTTP %{http_code}' -X POST "$FLEET_URL/api/sessions/{{Receiver}}/prompt" -H 'content-type: application/json' -d '{"text":"{{Note}}"}'""";
        return new ScriptedToolCall("call_curl", "bash", JsonSerializer.Serialize(new { command = curl, description = "Message the docs session" }));
    }

    private static async Task RunAsync(
        bool messagesOn,
        ScriptedToolCall senderCall,
        bool expectDelivery,
        Action<IReadOnlyList<string>, IReadOnlyList<BroadcastEvent>> assert)
    {
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
        for (var i = 0; i < 20; i++)
        {
            llm.Queue.Enqueue(request =>
            {
                if (LastRole(request) == "tool")
                    return new ScriptedLlmResponse { Text = "Done." };
                if (LastUserText(request) == SenderPrompt)
                    return new ScriptedLlmResponse { StopReason = "tool_calls", ToolCalls = [senderCall] };
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

            await sender.SendPromptAsync(SenderPrompt, null, ct);

            // Done when the sender has read its tool result and the receiver has answered, if it was reached.
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForAsync(
                    () => llm.Queue.Requests.Where(OffersTools).ToList(),
                    requests => requests.Any(r => LastRole(r) == "tool" && FirstUserText(r) == SenderPrompt)
                        && (!expectDelivery || requests.Any(r => LastUserText(r) is { } text && text.Contains(Note))),
                    wait.Token);
                await Task.Delay(1000, ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "Timed out. LLM requests:\n" + string.Join("\n", llm.Queue.Requests.Select(r => $"first={FirstUserText(r)} last={LastRole(r)}:{Trim(LastUserText(r) ?? LastToolText(r))}")));
            }

            // Only turns count: OpenCode's title requests carry the same text but offer no tools.
            assert([.. llm.Queue.Requests.Where(OffersTools)], [.. events]);

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
