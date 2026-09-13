extern alias FakeLlm;

using System.Collections.Concurrent;
using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Progress;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// Session progress end to end with a real <c>opencode</c> and a scripted model in a pooled session: todo lists
/// from <c>todowrite</c>, and plans from files the model writes and ticks with <c>write</c> and <c>edit</c>.
/// These also pin the shapes of OpenCode's todo event and file tool parts: if an OpenCode release changes
/// them, these tests fail. The pooled process runs with a scratch HOME, so it never reads or writes the real
/// user's data.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionProgressLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "progress-live";
    private const string InstanceId = "inst-progress-live";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public Task A_pooled_sessions_todo_list_reaches_the_row_and_the_open_session()
        => RunAsync(
            "Drop the dead tables.",
            queue =>
            {
                queue.Enqueue(ToolCall("call_todo_1", "todowrite", """
                    {"todos":[{"content":"Write the migration","status":"in_progress","priority":"high"},{"content":"Drop the indexes","status":"pending","priority":"medium"}]}
                    """));
                queue.Enqueue(ToolCall("call_todo_2", "todowrite", """
                    {"todos":[{"content":"Write the migration","status":"completed","priority":"high"},{"content":"Drop the indexes","status":"in_progress","priority":"medium"}]}
                    """));
            },
            list => list.Any(e => e.Payload.GetProperty("done").GetInt32() == 1),
            async (services, session, events, _, ct) =>
            {
                // Two row summaries on "sessions", one per todowrite call.
                var rows = events.Where(e => e.Type == SessionProgressTracker.SummaryEventType).ToList();
                rows.ShouldAllBe(e => e.Topic == "sessions");
                rows.Select(e => e.Payload.GetRawText()).ShouldBe(
                [
                    """{"sessionId":"progress-live","kind":"todos","done":0,"total":2,"current":"Write the migration"}""",
                    """{"sessionId":"progress-live","kind":"todos","done":1,"total":2,"current":"Drop the indexes"}""",
                ]);

                // The open session gets the whole list on its own topic.
                var detail = events.Last(e => e.Type == SessionProgressTracker.DetailEventType);
                detail.Topic.ShouldBe($"session:{SessionId}");
                detail.Payload.GetProperty("todos").EnumerateArray()
                    .Select(todo => (todo.GetProperty("content").GetString(), todo.GetProperty("status").GetString()))
                    .ShouldBe([("Write the migration", TodoStatuses.Completed), ("Drop the indexes", TodoStatuses.InProgress)]);

                // Stored for the owner, so the list and GET /progress have it after a restart.
                var stored = await StoredAsync(services, ct);
                (stored.Kind, stored.Done, stored.Total).ShouldBe((SessionProgressKinds.Todos, 1, 2));

                // OpenCode's GET /session/{id}/todo agrees, which is what Fleet asks when it has nothing stored.
                var todos = await session.GetTodosAsync(ct);
                todos.ShouldNotBeNull();
                todos.Select(todo => todo.Status).ShouldBe([TodoStatuses.Completed, TodoStatuses.InProgress]);

                // OpenCode's event name never reaches clients.
                events.ShouldNotContain(e => e.Type == "todo.updated");
            });

    [OpenCodeFact]
    public Task A_plan_file_the_model_writes_and_ticks_becomes_the_sessions_progress()
        => RunAsync(
            "Plan the work, then start on it.",
            queue =>
            {
                queue.Enqueue(ToolCall("call_write", "write", JsonSerializer.Serialize(new
                {
                    filePath = ".weave/plans/drop-tables.md",
                    content = "# Drop the dead tables\n\n## Tasks\n\n- [ ] 1. Write the migration\n- [ ] 2. Drop the indexes\n- [ ] 3. Start the app on a fresh database\n",
                })));
                queue.Enqueue(ToolCall("call_read", "read", """{"filePath":".weave/plans/drop-tables.md"}"""));
                queue.Enqueue(ToolCall("call_edit", "edit", """{"filePath":".weave/plans/drop-tables.md","oldString":"- [ ] 1. Write the migration","newString":"- [x] 1. Write the migration"}"""));
            },
            list => list.Any(e => e.Payload.GetProperty("kind").GetString() == "plan" && e.Payload.GetProperty("done").GetInt32() == 1),
            async (services, _, events, workspace, ct) =>
            {
                // Written: a plan at 0 of 3. Ticked: 1 of 3.
                var rows = events.Where(e => e.Type == SessionProgressTracker.SummaryEventType).Select(e => e.Payload.GetRawText()).ToList();
                rows.ShouldBe(
                [
                    """{"sessionId":"progress-live","kind":"plan","done":0,"total":3,"current":"Write the migration"}""",
                    """{"sessionId":"progress-live","kind":"plan","done":1,"total":3,"current":"Drop the indexes"}""",
                ]);

                // The tick has the time Fleet saw it and the message of the edit that made it.
                var stored = await StoredAsync(services, ct);
                var plan = stored.Plans.ShouldHaveSingleItem();
                (plan.Path, plan.Title).ShouldBe((".weave/plans/drop-tables.md", "Drop the dead tables"));
                var first = plan.Steps.First();
                first.Checked.ShouldBeTrue();
                first.TickedAt.ShouldNotBeNull();
                first.TickedInMessageId.ShouldNotBeNullOrWhiteSpace();

                File.ReadAllText(Path.Combine(workspace, ".weave", "plans", "drop-tables.md")).ShouldContain("- [x] 1. Write the migration");
            });

    [OpenCodeFact]
    public Task A_subagent_started_for_a_step_shows_its_own_todo_counts_under_that_step()
        => RunAsync(
            "Plan the work, then hand the first step to a subagent.",
            queue =>
            {
                queue.Enqueue(ToolCall("call_write", "write", JsonSerializer.Serialize(new
                {
                    filePath = ".weave/plans/drop-tables.md",
                    content = "# Drop the dead tables\n\n- [ ] 1. Write the migration\n- [ ] 2. Drop the indexes\n- [ ] 3. Start the app\n",
                })));
                queue.Enqueue(ToolCall("call_task", "task", """
                    {"description":"Write the migration","prompt":"Write the migration that drops the dead tables.","subagent_type":"general"}
                    """));

                // The subagent's turn. OpenCode 1.18 doesn't offer todowrite to subagents, so it has no counts of its own.
                queue.Enqueue(new ScriptedLlmResponse { Text = "The migration is written." });
            },
            _ => true,
            async (services, _, _, _, ct) =>
            {
                // The parent shows the subagent under step 1, titled with its task, linked to its session, finished.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                SessionProgress? stored;
                try
                {
                    stored = await WaitForAsync(
                        () => StoredOrNull(services),
                        progress => progress?.Subagents is [{ Status: "completed" }],
                        wait.Token);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"The subagent never showed as finished. Parent progress: {JsonSerializer.Serialize(StoredOrNull(services))}\n" +
                        $"Sessions: {JsonSerializer.Serialize(AllSessions(services))}");
                }

                (stored!.Kind, stored.Done, stored.Total).ShouldBe((SessionProgressKinds.Plan, 0, 3));
                var subagent = stored.Subagents.ShouldHaveSingleItem();
                (subagent.Agent, subagent.Title, subagent.StepKey, subagent.Status)
                    .ShouldBe(("general", "Write the migration", "1", "completed"));
                subagent.ChildSessionId.ShouldNotBeNullOrWhiteSpace();
                AllSessions(services).ShouldContain(row => row.StartsWith(subagent.ChildSessionId + " parent=progress-live", StringComparison.Ordinal));
            });

    /// <summary>
    /// Starts Fleet on Kestrel with a pooled opencode on the fake model, sends <paramref name="prompt"/>, waits until
    /// a row summary satisfies <paramref name="done"/>, then hands everything to <paramref name="assert"/>.
    /// </summary>
    private static async Task RunAsync(
        string prompt,
        Action<ScriptedResponseStore> script,
        Func<IReadOnlyList<BroadcastEvent>, bool> done,
        Func<IServiceProvider, IHarnessSession, IReadOnlyList<BroadcastEvent>, string, CancellationToken, Task> assert)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-progress-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, WriteNoOpPlugin(root));
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Progress" };
        script(llm.Queue);
        llm.Queue.Enqueue(new ScriptedLlmResponse { Text = "Done." });

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            SeedSession(services, workspace);

            var events = new ConcurrentQueue<BroadcastEvent>();
            var collecting = CollectAsync(services.GetRequiredService<IEventBroadcaster>(), events, ct);

            var runtime = services.GetRequiredService<OpenCodeHarnessRuntime>();
            await using var session = await runtime.SpawnAsync(
                new HarnessSpawnOptions
                {
                    SessionId = SessionId,
                    WorkingDirectory = workspace,
                    OwnerUserId = Owner,
                    LaunchArtifacts = new OpenCodeLaunchArtifacts(processEnvironment),
                },
                ct);

            RegisterLikeTheOrchestrator(services, session);
            await session.WaitForEventSubscriptionAsync(ct);

            await session.SendPromptAsync(prompt, null, ct);

            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForAsync(
                    () => events.Where(e => e.Type == SessionProgressTracker.SummaryEventType).ToList(),
                    done,
                    wait.Token);
            }
            catch (TimeoutException)
            {
                var messages = await session.GetMessagesAsync(null, CancellationToken.None);
                throw new TimeoutException(
                    $"Timed out waiting for progress. Fleet sent: {string.Join(", ", events.Select(e => $"{e.Type} {e.Payload.GetRawText()}"))}\n" +
                    $"OpenCode messages: {JsonSerializer.Serialize(messages)}\n" +
                    $"LLM requests: {llm.Queue.Requests.Count}");
            }

            try
            {
                await assert(services, session, [.. events], workspace, ct);
            }
            catch (TimeoutException ex)
            {
                var requests = llm.Queue.Requests.Select((request, index) =>
                {
                    using var body = JsonDocument.Parse(request);
                    var tools = body.RootElement.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
                        ? string.Join(",", t.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()))
                        : "-";
                    var messages = body.RootElement.GetProperty("messages");
                    var last = messages[messages.GetArrayLength() - 1].GetRawText();
                    return $"#{index} tools=[{tools}] last={last[..Math.Min(last.Length, 300)]}";
                });
                throw new TimeoutException($"{ex.Message}\nLLM requests:\n{string.Join("\n", requests)}", ex);
            }

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Points the seeded session at the spawned instance and registers it, as the orchestrator does, so the relay
    /// pumps it and subagent sessions are created under it.
    /// </summary>
    private static void RegisterLikeTheOrchestrator(IServiceProvider services, IHarnessSession session)
    {
        using (var scope = services.CreateScope())
        using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        using (var command = connection.CreateCommand())
        {
            var token = session.ResumeToken is null ? "NULL" : $"'{session.ResumeToken}'";
            command.CommandText = $"""
                INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
                VALUES ('{session.InstanceId}', 0, NULL, '', '', 'running', '2026-09-13T00:00:00+00:00', '{Owner}');
                UPDATE sessions SET instance_id = '{session.InstanceId}', harness_resume_token = {token} WHERE id = '{SessionId}';
                """;
            command.ExecuteNonQuery();
        }

        services.GetRequiredService<InstanceTracker>().Register(session.InstanceId, session);
    }

    private static SessionProgress? StoredOrNull(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner);
        return scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>()
            .GetAsync(SessionId, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static List<string> AllSessions(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id || ' parent=' || IFNULL(parent_session_id, '-') || ' title=' || IFNULL(title, '-') || ' user=' || user_id FROM sessions";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private static ScriptedLlmResponse ToolCall(string id, string tool, string arguments)
        => new() { StopReason = "tool_calls", ToolCalls = [new ScriptedToolCall(id, tool, arguments.Trim())] };

    private static async Task<SessionProgress> StoredAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        using var user = scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner);
        var stored = await scope.ServiceProvider.GetRequiredService<ISessionProgressRepository>().GetAsync(SessionId, ct);
        return stored.ShouldNotBeNull();
    }

    /// <summary>The shared scratch home loads one plugin of the user's own; this one adds nothing.</summary>
    private static string WriteNoOpPlugin(string root)
    {
        var path = Path.Combine(root, "no-op.ts");
        File.WriteAllText(path, "export const NoOp = async () => ({})\n");
        return path;
    }

    private static void SeedSession(IServiceProvider services, string workspace)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-progress', '{workspace}', 'Live', '2026-09-13T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('{InstanceId}', 0, NULL, '{workspace}', '', 'running', '2026-09-13T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, harness_type, created_at, user_id)
            VALUES ('{SessionId}', 'ws-progress', '{InstanceId}', 'pending', 'Live', 'active', '{workspace}',
                    'running', 'active', 'opencode', '2026-09-13T00:00:00+00:00', '{Owner}');
            """;
        command.ExecuteNonQuery();
    }

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

    private static async Task CollectAsync(IEventBroadcaster broadcaster, ConcurrentQueue<BroadcastEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var e in broadcaster.SubscribeAsync(["sessions", $"session:{SessionId}"], Owner, ct))
                events.Enqueue(e);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
