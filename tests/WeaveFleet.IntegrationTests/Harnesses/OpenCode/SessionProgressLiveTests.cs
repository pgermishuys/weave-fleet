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
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// Session progress end to end with a real <c>opencode</c>: a scripted model calls <c>todowrite</c> twice in
/// a pooled session, OpenCode sends <c>todo.updated</c>, and Fleet pushes the row summary and the todo list.
/// This also pins the shape of OpenCode's todo event: if an OpenCode release changes it, this test fails.
/// The pooled process runs with a scratch HOME, so it never reads or writes the real user's data.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionProgressLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "progress-live";
    private const string InstanceId = "inst-progress-live";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task A_pooled_sessions_todo_list_reaches_the_row_and_the_open_session()
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
        ScriptModel(llm.Queue);

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

            // The relay pumps registered instances, as it does for sessions the orchestrator starts.
            services.GetRequiredService<InstanceTracker>().Register(InstanceId, session);
            await session.WaitForEventSubscriptionAsync(ct);

            await session.SendPromptAsync("Drop the dead tables.", null, ct);

            List<BroadcastEvent> rows;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                rows = await WaitForAsync(
                    () => events.Where(e => e.Type == SessionProgressTracker.SummaryEventType).ToList(),
                    list => list.Any(e => e.Payload.GetProperty("done").GetInt32() == 1),
                    wait.Token);
            }
            catch (TimeoutException)
            {
                var messages = await session.GetMessagesAsync(null, CancellationToken.None);
                throw new TimeoutException(
                    $"Timed out waiting for progress. Fleet sent: {string.Join(", ", events.Select(e => e.Type))}\n" +
                    $"OpenCode messages: {JsonSerializer.Serialize(messages)}\n" +
                    $"LLM requests: {llm.Queue.Requests.Count}");
            }

            // Two row summaries on "sessions", one per todowrite call.
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
            using (var scope = services.CreateScope())
            using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            {
                var stored = await scope.ServiceProvider.GetRequiredService<WeaveFleet.Domain.Repositories.ISessionProgressRepository>()
                    .GetAsync(SessionId, ct);
                stored.ShouldNotBeNull();
                (stored.Kind, stored.Done, stored.Total).ShouldBe((SessionProgressKinds.Todos, 1, 2));
            }

            // OpenCode's GET /session/{id}/todo agrees, which is what Fleet asks when it has nothing stored.
            var todos = await session.GetTodosAsync(ct);
            todos.ShouldNotBeNull();
            todos.Select(todo => todo.Status).ShouldBe([TodoStatuses.Completed, TodoStatuses.InProgress]);

            // OpenCode's event name never reaches clients.
            events.ShouldNotContain(e => e.Type == "todo.updated");

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
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

    private static void ScriptModel(ScriptedResponseStore queue)
    {
        queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Drop the dead tables" };

        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls =
            [
                new ScriptedToolCall("call_todo_1", "todowrite", """
                    {"todos":[{"content":"Write the migration","status":"in_progress","priority":"high"},{"content":"Drop the indexes","status":"pending","priority":"medium"}]}
                    """),
            ],
        });

        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls =
            [
                new ScriptedToolCall("call_todo_2", "todowrite", """
                    {"todos":[{"content":"Write the migration","status":"completed","priority":"high"},{"content":"Drop the indexes","status":"in_progress","priority":"medium"}]}
                    """),
            ],
        });

        queue.Enqueue(new ScriptedLlmResponse { Text = "Done." });
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
