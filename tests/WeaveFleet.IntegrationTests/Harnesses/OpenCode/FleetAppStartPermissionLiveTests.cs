extern alias FakeLlm;

using System.Text.Json.Nodes;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// <c>fleet_app_start</c> runs a shell command, so an agent that may not run shell commands may not call it
/// either. A real <c>opencode</c> loads Fleet's plugin; a scripted model, running as an agent whose shell
/// permission is denied, calls <c>fleet_app_start</c> with a command that leaves a file behind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FleetAppStartPermissionLiveTests
{
    private const string Owner = "local-user";
    private const string SessionId = "fleet-app-start-permission-live";
    private const string ReadOnlyAgent = "readonly";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [OpenCodeFact]
    public async Task An_agent_that_may_not_run_shell_commands_cannot_run_one_through_fleet_app_start()
    {
        var (ran, answer) = await CallAppStartAsync(ReadOnlyAgent);

        ran.ShouldBeFalse("fleet_app_start ran a command for an agent that may not run shell commands.");
        answer.ShouldNotContain("before serving a page");
    }

    [OpenCodeFact]
    public async Task An_agent_that_may_run_shell_commands_still_runs_its_command_through_fleet_app_start()
    {
        var (ran, _) = await CallAppStartAsync(agent: null);

        ran.ShouldBeTrue("fleet_app_start didn't run the command for an agent that may run shell commands.");
    }

    /// <summary>
    /// The same through Weave: its OpenCode adapter gives the read-only reviewer, <c>weft</c>, <c>bash: deny</c>.
    /// Set <c>WEAVE_OPENCODE_ADAPTER</c> to the adapter's <c>dist/plugin.js</c> to run it; it passes otherwise.
    /// </summary>
    [OpenCodeFact]
    public async Task Weaves_read_only_reviewer_cannot_run_a_command_through_fleet_app_start()
    {
        if (Environment.GetEnvironmentVariable("WEAVE_OPENCODE_ADAPTER") is not { Length: > 0 } adapter || !File.Exists(adapter))
            return;

        var (ran, _) = await CallAppStartAsync("weft", weaveAdapter: adapter);

        ran.ShouldBeFalse("fleet_app_start ran a command for Weave's weft, which may not run shell commands.");
    }

    /// <summary>
    /// A scripted model calls <c>fleet_app_start</c> once as <paramref name="agent"/>, with a command that leaves
    /// a file behind. Returns whether the command ran, and the request that carried the tool's answer.
    /// </summary>
    private static async Task<(bool Ran, string Answer)> CallAppStartAsync(string? agent, string? weaveAdapter = null)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-app-start-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var marker = Path.Combine(workspace, "ran.txt");

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var processEnvironment = WriteScratchOpenCodeHome(root, llm.BaseUrl, weaveAdapter);
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Diff" };
        llm.Queue.Enqueue(new ScriptedLlmResponse
        {
            StopReason = "tool_calls",
            ToolCalls = [new ScriptedToolCall("call_app", "fleet_app_start", """{"command":"echo ran> ran.txt","title":"diff"}""")],
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

            await session.SendPromptAsync("Show me the diff.", new PromptOptions { Agent = agent }, ct);

            // The model gets the tool's answer in its next request.
            List<string> requests;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(60));
                requests = await WaitForAsync(() => llm.Queue.Requests.ToList(), list => list.Any(r => r.Contains("call_app")), wait.Token);
            }
            catch (TimeoutException)
            {
                var messages = await session.GetMessagesAsync(null, CancellationToken.None);
                throw new TimeoutException(
                    $"The model never got fleet_app_start's answer. OpenCode messages: {System.Text.Json.JsonSerializer.Serialize(messages)}\n" +
                    $"LLM requests: {llm.Queue.Requests.Count}");
            }
            // Fleet answers only once a command it ran has exited, so by now its file is there if it ran.
            return (File.Exists(marker), requests.First(r => r.Contains("call_app")));
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// A scratch OpenCode home with one extra agent that may read but not run shell commands: from OpenCode's own
    /// config, or with <paramref name="weaveAdapter"/>, Weave's built-in weft (the scratch Weave config only points
    /// it at the fake model).
    /// </summary>
    private static Dictionary<string, string> WriteScratchOpenCodeHome(string root, Uri llmBaseUrl, string? weaveAdapter)
    {
        if (weaveAdapter is not null)
        {
            var weaveEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llmBaseUrl, weaveAdapter);
            var weaveHome = Path.Combine(weaveEnvironment["HOME"], ".weave");
            Directory.CreateDirectory(weaveHome);
            File.WriteAllText(Path.Combine(weaveHome, "config.weave"), """
                agent weft {
                  models ["fake/fake-model"]
                  mode all
                }
                """);
            return weaveEnvironment;
        }

        var noPlugin = Path.Combine(root, "no-plugin.ts");
        File.WriteAllText(noPlugin, "export const NoPlugin = async () => ({})\n");

        var environment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llmBaseUrl, noPlugin);
        var configPath = Path.Combine(environment["XDG_CONFIG_HOME"], "opencode", "opencode.json");
        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        config["agent"] = new JsonObject
        {
            [ReadOnlyAgent] = new JsonObject
            {
                ["mode"] = "primary",
                ["permission"] = new JsonObject { ["bash"] = "deny", ["edit"] = "deny" },
            },
        };
        File.WriteAllText(configPath, config.ToJsonString());
        return environment;
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
