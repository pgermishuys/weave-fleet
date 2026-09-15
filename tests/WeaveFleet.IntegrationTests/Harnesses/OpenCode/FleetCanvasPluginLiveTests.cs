extern alias FakeLlm;

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FakeLlm::FakeLlmServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// The canvas tools end to end with a real <c>opencode</c>: Fleet on Kestrel spawns a pooled process, which
/// loads the Fleet plugin next to the user's own. A scripted model calls <c>fleet_canvas_open</c>, then
/// <c>fleet_canvas_patch</c> with the id the first call returned. The pooled process runs with a scratch
/// HOME and scratch XDG dirs, so it never reads or writes the real user's OpenCode or Fleet data.
/// </summary>
[Trait("Category", "Integration")]
public sealed partial class FleetCanvasPluginLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "fleet-canvas-live";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task A_pooled_session_opens_and_patches_a_canvas_through_the_plugin()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-canvas-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var processEnvironment = WriteScratchOpenCodeHome(root, llm.BaseUrl);
        ScriptModel(llm.Queue);

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        List<int> pooledProcessIds = [];
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

            await session.SendPromptAsync("Draw the session event flow, then add the client.", null, ct);

            List<BroadcastEvent> updated;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(60));
                updated = await WaitForAsync(() => events.Where(e => e.Type == "canvas.updated").ToList(), list => list.Count >= 2, wait.Token);
            }
            catch (TimeoutException)
            {
                var messages = await session.GetMessagesAsync(null, CancellationToken.None);
                throw new TimeoutException(
                    $"Timed out waiting for canvas events. Fleet sent: {string.Join(", ", events.Select(e => e.Type))}\n" +
                    $"OpenCode messages: {JsonSerializer.Serialize(messages)}\n" +
                    $"LLM requests: {llm.Queue.Requests.Count}");
            }

            // Two canvas.updated events on session:{id}: the open at v1, then the patch at v2.
            updated.Select(e => e.Payload.GetProperty("version").GetInt32()).ShouldBe([1, 2]);
            updated.ShouldAllBe(e => e.Topic == $"session:{SessionId}" && e.Payload.GetProperty("actor").GetString() == "agent");
            updated[1].Payload.GetProperty("summary").GetString().ShouldBe("+1 box");

            // The stored canvas is at v2, owned by the session owner.
            using (var scope = services.CreateScope())
            using (scope.ServiceProvider.GetRequiredService<IBackgroundUserScope>().Begin(Owner))
            {
                var canvas = (await scope.ServiceProvider.GetRequiredService<ICanvasService>().ListAsync(SessionId, ct)).ShouldHaveSingleItem().Canvas;
                canvas.Version.ShouldBe(2);
                canvas.UserId.ShouldBe(Owner);
                DiagramState.Parse(canvas.StateJson).Nodes.Select(n => n.Id).ShouldBe(["n1", "n2", "n3"]);
            }

            // The model saw Fleet's tools and the user's own plugin tool side by side.
            var offered = OfferedToolNames(llm.Queue.Requests);
            offered.ShouldContain("fleet_canvas_list");
            offered.ShouldContain("fleet_canvas_open");
            offered.ShouldContain("fleet_canvas_read");
            offered.ShouldContain("fleet_canvas_patch");
            offered.ShouldContain("fleet_canvas_focus");
            offered.ShouldContain("user_probe");

            // The plugin added Fleet's skills without dropping the user's own skill path.
            var turn = llm.Queue.Requests.First(request => OfferedToolNames([request]).Count > 0);
            turn.ShouldContain("fleet-api");
            turn.ShouldContain("user-probe-skill");

            // A refused call reaches the model as Fleet's own message.
            await WaitForAsync(() => llm.Queue.Requests, requests => requests.Count(r => OfferedToolNames([r]).Count > 0) >= 4, ct);
            llm.Queue.Requests[^1].ShouldContain("No canvas cv_missing in this session. Call fleet_canvas_list to see the open canvases.");

            var dataDirectory = Path.GetDirectoryName(dbPath)!;
            File.Exists(Path.Combine(dataDirectory, "opencode", OpenCodeFleetPlugin.FileName)).ShouldBeTrue();
            File.Exists(Path.Combine(dataDirectory, "opencode", "skills", "fleet-api", "SKILL.md")).ShouldBeTrue();

            await cts.CancelAsync();
            await collecting;
        }
        finally
        {
            if (factory.IsStarted)
            {
                pooledProcessIds = factory.LiveServices.GetRequiredService<OpenCodeHarnessRuntime>()
                    .GetPooledOpenCodePoolHealth().Instances
                    .Select(instance => instance.ProcessId)
                    .OfType<int>()
                    .ToList();
            }

            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }

        // Stopping Fleet stops the pooled process too.
        pooledProcessIds.ShouldNotBeEmpty();
        foreach (var processId in pooledProcessIds)
            (await HasExitedAsync(processId, TimeSpan.FromSeconds(5))).ShouldBeTrue($"opencode process {processId} outlived Fleet.");
    }

    [OpenCodeFact]
    public async Task A_pooled_session_loads_the_fleet_api_skill_and_reaches_Fleet_with_it()
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-skill-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var processEnvironment = WriteScratchOpenCodeHome(root, llm.BaseUrl);
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Fleet API" };
        llm.Queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls = [new ScriptedToolCall("call_skill", "skill", """{"name":"fleet-api"}""")],
        });
        llm.Queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls =
            [
                new ScriptedToolCall("call_curl", "bash", $$"""
                    {"command":"curl -s \"$FLEET_URL/api/sessions/{{SessionId}}\"","description":"Read this session from Fleet"}
                    """),
            ],
        });
        llm.Queue.Enqueue(new ScriptedLlmResponse { Text = "Done." });

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            SeedSession(services, workspace);

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

            await session.SendPromptAsync("How is this session doing in Fleet?", null, ct);

            // The skill's own text reached the model, then the API's answer about this session, fetched with no token.
            var requests = await WaitForAsync(() => llm.Queue.Requests.ToList(), list => list.Count(r => OfferedToolNames([r]).Count > 0) >= 3, ct);
            requests.ShouldContain(request => request.Contains("Look it up, don't guess"));
            requests.ShouldContain(request => request.Contains("workspaceDirectory") && request.Contains("activityStatus"));
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<bool> HasExitedAsync(int processId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Loads one plugin and one skill path of the user's own next to Fleet's, and returns the env for the pooled
    /// process.
    /// </summary>
    private static Dictionary<string, string> WriteScratchOpenCodeHome(string root, Uri llmBaseUrl)
    {
        var userPlugin = Path.Combine(root, "user-probe.ts");
        File.WriteAllText(userPlugin, """
            export const UserProbe = async () => ({
              tool: {
                user_probe: { description: "A tool from the user's own plugin.", args: {}, execute: async () => "ok" },
              },
            })
            """);

        var userSkills = Path.Combine(root, "user-skills");
        Directory.CreateDirectory(Path.Combine(userSkills, "user-probe-skill"));
        File.WriteAllText(Path.Combine(userSkills, "user-probe-skill", "SKILL.md"), """
            ---
            name: user-probe-skill
            description: A skill from the user's own skill path.
            ---
            Nothing to do.
            """);

        var environment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llmBaseUrl, userPlugin);
        var configPath = Path.Combine(environment["XDG_CONFIG_HOME"], "opencode", "opencode.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config["skills"] = new JsonObject { ["paths"] = new JsonArray(userSkills) };
        File.WriteAllText(configPath, config.ToJsonString());
        return environment;
    }

    private static void ScriptModel(ScriptedResponseStore queue)
    {
        queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Session event flow" };

        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls =
            [
                new ScriptedToolCall("call_open", "fleet_canvas_open", """
                    {"kind":"diagram","title":"Session event flow","state":{"nodes":[{"id":"n1","label":"NuCode session"},{"id":"n2","label":"SessionEventsHub"}],"edges":[{"id":"e1","from":"n1","to":"n2","label":"publishes"}]}}
                    """),
            ],
        });

        // The patch needs the canvas id, which only the open's tool result knows.
        queue.Enqueue(request => CanvasId().Match(request) is { Success: true } match
            ? new ScriptedLlmResponse
            {
                StopReason = "tool_calls",
                ToolCalls =
                [
                    new ScriptedToolCall("call_patch", "fleet_canvas_patch", $$"""
                        {"canvasId":"{{match.Value}}","ops":[{"op":"addNode","id":"n3","label":"Client"}]}
                        """),
                ],
            }
            : new ScriptedLlmResponse { Text = "The open didn't return a canvas id." });

        // A failing call: the plugin throws Fleet's message, and the model reads it in the next request.
        queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls = [new ScriptedToolCall("call_read", "fleet_canvas_read", """{"canvasId":"cv_missing"}""")],
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
            VALUES ('ws-live', '{workspace}', 'Live', '2026-09-12T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-live', 0, NULL, '{workspace}', '', 'running', '2026-09-12T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id)
            VALUES ('{SessionId}', 'ws-live', 'inst-live', 'pending', 'Live', 'active', '{workspace}',
                    'running', 'active', '2026-09-12T00:00:00+00:00', '{Owner}');
            """;
        command.ExecuteNonQuery();
    }

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

    [GeneratedRegex("cv_[0-9A-Z]{26}")]
    private static partial Regex CanvasId();
}

/// <summary>A fact that's skipped when the <c>opencode</c> binary isn't on PATH.</summary>
internal sealed class OpenCodeFactAttribute : FactAttribute
{
    public OpenCodeFactAttribute()
    {
        if (!IsOpenCodeOnPath())
            Skip = "opencode isn't on PATH.";
    }

    private static bool IsOpenCodeOnPath()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "opencode",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            return process is not null && process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
