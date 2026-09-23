extern alias FakeLlm;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// Navigating away from a session and back rebuilds it from OpenCode's history. With a real <c>opencode</c> and a
/// scripted model, a turn delegates to a sub-agent and draws a diagram; the rebuilt session must show every part
/// exactly as the live stream did, with the sub-agent card still linked to its delegation. The pooled process runs
/// with a scratch HOME and scratch XDG dirs, so it never reads or writes the real user's OpenCode or Fleet data.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReopenedSessionLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "fleet-reopen-live";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task A_reopened_session_shows_the_sub_agent_and_visual_cards_the_live_stream_showed()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-reopen-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, WriteDiagramPlugin(root));
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

            var session = await services.GetRequiredService<OpenCodeHarnessRuntime>().SpawnAsync(
                new HarnessSpawnOptions
                {
                    SessionId = SessionId,
                    WorkingDirectory = workspace,
                    OwnerUserId = Owner,
                    LaunchArtifacts = new OpenCodeLaunchArtifacts(processEnvironment),
                },
                ct);
            await using var spawned = session;
            RegisterLikeTheOrchestrator(services, session);

            await session.SendPromptAsync("Map the code with a sub-agent, then draw it.", null, ct);

            // The turn is done once the diagram's card completed and the sub-agent's delegation finished.
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(90));
                await WaitForAsync(
                    () => events.ToList(),
                    list => LiveToolParts(list).Any(p => p.ToolName == "draw_diagram" && p.State is ToolCompletedState)
                        && list.Any(IsFinishedDelegation)
                        && llm.Queue.Count == 0,
                    wait.Token);
            }
            catch (TimeoutException)
            {
                var messages = await session.GetMessagesAsync(null, CancellationToken.None);
                throw new TimeoutException(
                    $"Timed out waiting for the turn. Fleet sent: {string.Join(", ", events.Select(e => e.Type))}\n" +
                    $"OpenCode messages: {JsonSerializer.Serialize(messages)}\n" +
                    $"LLM requests: {llm.Queue.Requests.Count}, still queued: {llm.Queue.Count}");
            }

            // Navigating back: the client subscribes and gets this snapshot.
            SessionSnapshot snapshot;
            using (var scope = services.CreateScope())
            using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            {
                snapshot = await scope.ServiceProvider.GetRequiredService<ISessionMessageProxy>().GetSnapshotAsync(SessionId, ct: ct);
            }

            snapshot.IsPartial.ShouldBeFalse();
            var reopenedTools = snapshot.Messages.SelectMany(m => m.Parts).OfType<ToolMessageEventPart>().ToList();
            reopenedTools.Select(t => t.ToolName).ShouldBe(["task", "draw_diagram"], ignoreOrder: true);

            // Each tool card comes back as the live stream last showed it.
            var liveById = LiveToolParts(events.ToList()).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Last());
            foreach (var reopened in reopenedTools)
            {
                liveById.ShouldContainKey(reopened.Id, $"The live stream never sent part {reopened.Id} ({reopened.ToolName}).");
                JsonNode.DeepEquals(ToJson(reopened), ToJson(liveById[reopened.Id]))
                    .ShouldBeTrue($"{reopened.ToolName} differs.\nReopened: {ToJson(reopened)}\nLive:     {ToJson(liveById[reopened.Id])}");
            }

            // The sub-agent card: a delegation names the task call and the child session.
            var task = reopenedTools.Single(t => t.ToolName == "task");
            var delegation = snapshot.Delegations.ShouldHaveSingleItem();
            delegation.ParentToolCallId.ShouldBe(task.CallId);
            delegation.ChildSessionId.ShouldNotBeNullOrWhiteSpace();
            task.State.ShouldBeOfType<ToolCompletedState>().Metadata.ShouldNotBeNull();

            // The visual card: the diagram's payload is still there to render.
            var visual = reopenedTools.Single(t => t.ToolName == "draw_diagram").State.ShouldBeOfType<ToolCompletedState>();
            visual.Output.ShouldNotBeNull().GetString().ShouldNotBeNull().ShouldContain("\"$type\":\"visual/flow\"");

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A scratch, generic diagram-drawing tool, as a plugin so a scratch HOME needs no package install.</summary>
    private static string WriteDiagramPlugin(string root)
    {
        var path = Path.Combine(root, "diagram-probe.ts");
        File.WriteAllText(path, """
            export const DiagramProbe = async () => ({
              tool: {
                draw_diagram: {
                  description: "Render a visual diagram inline in the conversation.",
                  args: {},
                  execute: async () => JSON.stringify({
                    $type: "visual/flow",
                    content: { nodes: [{ id: "a", label: "Client" }, { id: "b", label: "Fleet" }], edges: [{ id: "e1", source: "a", target: "b" }] },
                    title: "Session flow",
                  }),
                },
              },
            })
            """);
        return path;
    }

    private static void ScriptModel(ScriptedResponseStore queue)
    {
        // Title generation, for the parent and the sub-agent's session.
        queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Map the code" };

        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls =
            [
                new ScriptedToolCall("call_task", "task", """
                    {"description":"Map the code","prompt":"List the main folders.","subagent_type":"general"}
                    """),
            ],
        });

        // The sub-agent's turn.
        queue.Enqueue(new ScriptedLlmResponse { Text = "src holds the server, client the web app." });

        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls = [new ScriptedToolCall("call_vis", "draw_diagram", "{}")],
        });

        queue.Enqueue(new ScriptedLlmResponse { Text = "Done." });
    }

    private static void SeedSession(IServiceProvider services, string workspace)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-reopen', '{workspace}', 'Live', '2026-09-13T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-seed', 0, NULL, '{workspace}', '', 'running', '2026-09-13T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, harness_type, created_at, user_id)
            VALUES ('{SessionId}', 'ws-reopen', 'inst-seed', 'pending', 'Live', 'active', '{workspace}',
                    'running', 'active', 'opencode', '2026-09-13T00:00:00+00:00', '{Owner}');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// What <c>SessionOrchestrator</c> does after a spawn: record the instance, map the session to it, then register it,
    /// which starts the relay pump and lets the snapshot read the live harness.
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

    private static bool IsFinishedDelegation(BroadcastEvent e)
        => e.Type == "delegation.updated"
            && e.Payload.TryGetProperty("status", out var status)
            && status.GetString() == "completed";

    private static IEnumerable<ToolMessageEventPart> LiveToolParts(IEnumerable<BroadcastEvent> events)
        => events.Select(e => e.DomainEvent).OfType<MessagePartUpdated>().Select(e => e.Payload.Part).OfType<ToolMessageEventPart>();

    private static JsonNode ToJson(ToolMessageEventPart part)
        => JsonNode.Parse(JsonSerializer.Serialize(
            new MessagePartUpdatedPayload { SessionId = SessionId, Part = part },
            InfrastructureJsonContext.Default.MessagePartUpdatedPayload))!["part"]!;

    private static async Task CollectAsync(IEventBroadcaster broadcaster, ConcurrentQueue<BroadcastEvent> events, CancellationToken ct)
    {
        try
        {
            await foreach (var e in broadcaster.SubscribeAsync([$"session:{SessionId}"], Owner, ct))
                events.Enqueue(e);
        }
        catch (OperationCanceledException)
        {
        }
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
}
