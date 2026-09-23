extern alias FakeLlm;

using System.Text.Json;
using FakeLlm::FakeLlmServer;
using Microsoft.Extensions.DependencyInjection;
using WeaveFleet.Application.Data;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Infrastructure.Harnesses.OpenCode;
using WeaveFleet.Infrastructure.Services;

namespace WeaveFleet.IntegrationTests.Harnesses.OpenCode;

/// <summary>
/// The workflow step tool on a real <c>opencode</c>, with a scripted model: a session a workflow started sees
/// <c>fleet_step_done</c> and finishes its step with it; every other session on the same pooled process never has it
/// offered to its model, nor does the recap's fork of one. These fail if a future OpenCode changes how a session's
/// deny rule or a prompt's <c>tools</c> map hides a tool.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WorkflowStepToolLiveTests
{
    private const string Owner = "local-user";
    private const string Normal = "wf-normal";
    private const string Step = "wf-step";
    private const string RunId = "run-live";
    private const string NormalPrompt = "NORMAL: what does this folder hold?";
    private const string StepPrompt = "STEP: check the change, then finish the step.";
    private const string DelegatingStepPrompt = "STEP: ask a subagent to finish the step.";
    private const string ChildPrompt = "CHILD: finish the step for your parent.";
    private const string RecapPrompt = "RECAP: one line on where this is.";
    private const string DelegatingNormalPrompt = "NORMAL-DELEGATE: ask a subagent to look around.";
    private const string LookPrompt = "CHILD-LOOK: look around the folder.";
    private const string ChildAgainPrompt = "CHILD-AGAIN: and once more.";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private const string Workflow = """
        name: Live check
        steps:
          - id: check
            title: Check
            model: standard
            prompt: Check it.
            outcomes: [pass, changes]
        """;

    [OpenCodeFact]
    public async Task A_step_session_calls_fleet_step_done_and_other_sessions_never_see_it()
    {
        await RunAsync(workflowsOn: true, stepPrompt: StepPrompt, async (services, llm, normal, step, ct) =>
        {
            await normal.SendPromptAsync(NormalPrompt, null, ct);
            await step.SendPromptAsync(StepPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => FirstUserText(t) == NormalPrompt) && r.Any(t => LastRole(t) == "tool" && FirstUserText(t) == StepPrompt), ct);
            await WaitForAsync(() => RunStatus(services), status => status == WorkflowRunStatus.Done, ct);

            var turns = Turns(llm);
            OfferedToolNames(turns.Where(t => FirstUserText(t) == NormalPrompt)).ShouldNotContain(FleetWorkflows.StepTool);
            OfferedToolNames(turns.Where(t => FirstUserText(t) == NormalPrompt)).ShouldContain("fleet_canvas_list");
            OfferedToolNames(turns.Where(t => FirstUserText(t) == StepPrompt)).ShouldContain(FleetWorkflows.StepTool);
            LastToolResult(turns, StepPrompt).ShouldContain("Recorded outcome pass.");

            var visit = (await Repository(services, r => r.ListStepsAsync(RunId))).ShouldHaveSingleItem();
            (visit.Status, visit.Outcome, visit.Summary).ShouldBe((WorkflowRunStepStatus.Done, "pass", "Checked: the change is right."));

            // The recap's fork keeps the parent's tool list, so its request is read from the provider's cache.
            var recap = await normal.AskOffTheRecordAsync(RecapPrompt, ct);
            recap.ShouldBe("Recap.");
            var recapTurn = Turns(llm).Last(t => LastUserText(t) == RecapPrompt);
            OfferedToolNames([recapTurn]).ShouldBe(OfferedToolNames(turns.Where(t => FirstUserText(t) == NormalPrompt).Take(1)), ignoreOrder: true);
        });
    }

    [OpenCodeFact]
    public async Task A_step_sessions_subagent_cant_finish_the_step()
    {
        await RunAsync(workflowsOn: true, stepPrompt: DelegatingStepPrompt, async (services, llm, _, step, ct) =>
        {
            await step.SendPromptAsync(DelegatingStepPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => LastRole(t) == "tool" && FirstUserText(t) == ChildPrompt), ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => LastRole(t) == "tool" && FirstUserText(t) == DelegatingStepPrompt), ct);

            LastToolResult(Turns(llm), ChildPrompt).ShouldContain("Only the session Fleet started for a workflow step can finish it.");
            (await RunStatus(services)).ShouldBe(WorkflowRunStatus.Running);
        });
    }

    [OpenCodeFact]
    public async Task A_delegated_child_keeps_the_rules_its_agent_gave_it_when_Fleet_prompts_it()
    {
        await RunAsync(workflowsOn: true, stepPrompt: StepPrompt, async (services, llm, normal, _, environment, workspace, ct) =>
        {
            await normal.SendPromptAsync(DelegatingNormalPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => LastRole(t) == "tool" && FirstUserText(t) == DelegatingNormalPrompt), ct);
            var firstChildTurn = Turns(llm).First(t => FirstUserText(t) == LookPrompt);

            // The task tool's result names the child's OpenCode session.
            var childId = System.Text.RegularExpressions.Regex.Match(LastToolResult(Turns(llm), DelegatingNormalPrompt), @"ses_[A-Za-z0-9]+").Value;
            childId.ShouldNotBeNullOrEmpty();

            // Fleet wakes a child the way it does after a restart, and prompts it: its own rules must stay.
            using (var scope = services.CreateScope())
            using (var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    INSERT INTO sessions (id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                        lifecycle_status, retention_status, created_at, user_id, parent_session_id)
                    VALUES ('wf-child', 'ws-live', 'inst-live', 'pending', 'Look around', 'active', '{workspace}',
                            'running', 'active', '2026-09-23T00:00:00+00:00', '{Owner}', '{Normal}');
                    """;
                command.ExecuteNonQuery();
            }

            var runtime = services.GetRequiredService<OpenCodeHarnessRuntime>();
            await using var child = await runtime.ResumeAsync(new HarnessResumeOptions
            {
                SessionId = "wf-child",
                WorkingDirectory = workspace,
                OwnerUserId = Owner,
                ResumeToken = childId,
                LaunchArtifacts = new OpenCodeLaunchArtifacts(environment),
                ParentSessionId = Normal,
                DelegatedChild = true,
            }, ct);
            await child.SendPromptAsync(ChildAgainPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => LastUserText(t) == ChildAgainPrompt), ct);

            var again = Turns(llm).Last(t => LastUserText(t) == ChildAgainPrompt);
            // A tools map would replace the rules the task tool gave the child, and what it was offered would change.
            OfferedToolNames([again]).ShouldBe(OfferedToolNames([firstChildTurn]), ignoreOrder: true);
            // It inherits its parent's deny, so it never had the step tool either.
            OfferedToolNames([firstChildTurn, again]).ShouldNotContain(FleetWorkflows.StepTool);
        });
    }

    [OpenCodeFact]
    public async Task With_workflows_off_no_session_has_the_step_tool()
    {
        await RunAsync(workflowsOn: false, stepPrompt: StepPrompt, async (_, llm, normal, step, ct) =>
        {
            await normal.SendPromptAsync(NormalPrompt, null, ct);
            await step.SendPromptAsync(StepPrompt, null, ct);
            await WaitForAsync(() => Turns(llm), r => r.Any(t => FirstUserText(t) == NormalPrompt) && r.Any(t => FirstUserText(t) == StepPrompt), ct);
            await Task.Delay(2000, ct);

            OfferedToolNames(Turns(llm)).ShouldNotContain(FleetWorkflows.StepTool);
        });
    }

    private delegate Task Scenario(IServiceProvider services, FakeLlmServerFixture llm, IHarnessSession normal, IHarnessSession step, CancellationToken ct);

    private delegate Task ScenarioWithEnvironment(
        IServiceProvider services, FakeLlmServerFixture llm, IHarnessSession normal, IHarnessSession step, Dictionary<string, string> environment, string workspace, CancellationToken ct);

    private static Task RunAsync(bool workflowsOn, string stepPrompt, Scenario scenario)
        => RunAsync(workflowsOn, stepPrompt, (services, llm, normal, step, _, _, ct) => scenario(services, llm, normal, step, ct));

    private static async Task RunAsync(bool workflowsOn, string stepPrompt, ScenarioWithEnvironment scenario)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var root = Path.Combine(Path.GetTempPath(), $"fleet-workflows-live-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var dbPath = Path.Combine(root, "fleet", "fleet.db");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var llm = await FakeLlmServerFixture.StartAsync();
        var plugin = Path.Combine(root, "no-op.ts");
        File.WriteAllText(plugin, "export const NoOp = async () => ({})\n");
        var processEnvironment = PooledOpenCodeLiveHost.WriteScratchOpenCodeHome(root, llm.BaseUrl, plugin);

        // Several sessions share one model, so it answers by what it's asked, not by arrival order.
        llm.Queue.ToolLessResponse = new ScriptedLlmResponse { Text = "Live check" };
        for (var i = 0; i < 30; i++)
        {
            llm.Queue.Enqueue(request =>
            {
                if (LastUserText(request) == RecapPrompt)
                    return new ScriptedLlmResponse { Text = "Recap." };
                if (LastRole(request) == "tool")
                    return new ScriptedLlmResponse { Text = "Done." };
                if (LastUserText(request) is { } text && text.StartsWith(StepPrompt, StringComparison.Ordinal))
                    return ToolCall("call_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "Checked: the change is right." });
                if (LastUserText(request) is { } delegating && delegating.StartsWith(DelegatingStepPrompt, StringComparison.Ordinal))
                    return ToolCall("call_task", "task", new { description = "Finish the step", prompt = ChildPrompt, subagent_type = "general" });
                if (LastUserText(request) is { } delegatingNormal && delegatingNormal.StartsWith(DelegatingNormalPrompt, StringComparison.Ordinal))
                    return ToolCall("call_look", "task", new { description = "Look around", prompt = LookPrompt, subagent_type = "general" });
                if (LastUserText(request) is { } look && (look.StartsWith(LookPrompt, StringComparison.Ordinal) || look.StartsWith(ChildAgainPrompt, StringComparison.Ordinal)))
                    return new ScriptedLlmResponse { Text = "Looked." };
                if (LastUserText(request) is { } child && child.StartsWith(ChildPrompt, StringComparison.Ordinal))
                    return ToolCall("call_child_done", FleetWorkflows.StepTool, new { outcome = "pass", summary = "From the child." });
                return new ScriptedLlmResponse { Text = "It holds the workspace." };
            });
        }

        var factory = new PooledOpenCodeLiveHost.KestrelFleetFactory(dbPath);
        try
        {
            try { _ = factory.Services; }
            catch (InvalidCastException) { /* expected: the base class expects a TestServer */ }

            var services = factory.LiveServices;
            Seed(services, workspace);

            var runtime = services.GetRequiredService<OpenCodeHarnessRuntime>();
            if (workflowsOn)
            {
                using (BackgroundUserContext.BeginScope(Owner))
                using (var scope = services.CreateScope())
                    await scope.ServiceProvider.GetRequiredService<IUserPreferenceRepository>().SetAsync(FleetWorkflows.PreferenceKey, "true");
            }

            var prepared = await runtime.PrepareRuntimeAsync(
                new RuntimePreparationContext { UserId = Owner, UserCredentials = [], WorkingDirectory = workspace },
                ct);
            foreach (var (name, value) in ((OpenCodeLaunchArtifacts)prepared.ShouldBeOfType<RuntimePreparation.Ready>().Artifacts).EnvironmentVariables)
                processEnvironment[name] = value;
            processEnvironment.ContainsKey(FleetWorkflows.EnvironmentVariable).ShouldBe(workflowsOn);

            // Both on one pooled process: the step tool is in the process, and only the step keeps it.
            await using var normal = await SpawnAsync(runtime, Normal, workspace, processEnvironment, workflowStep: false, ct);
            await using var step = await SpawnAsync(runtime, Step, workspace, processEnvironment, workflowStep: true, ct);
            RegisterLikeTheOrchestrator(services, Normal, normal);
            RegisterLikeTheOrchestrator(services, Step, step);
            await normal.WaitForEventSubscriptionAsync(ct);
            await step.WaitForEventSubscriptionAsync(ct);

            try
            {
                await scenario(services, llm, normal, step, processEnvironment, workspace, ct);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    "Timed out. LLM requests:\n" + string.Join("\n", llm.Queue.Requests.Select(r => $"first={FirstUserText(r)} last={LastRole(r)}:{Trim(LastUserText(r) ?? LastToolText(r))}")));
            }

            await cts.CancelAsync();
        }
        finally
        {
            await factory.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static ScriptedLlmResponse ToolCall(string id, string tool, object args)
        => new() { StopReason = "tool_calls", ToolCalls = [new ScriptedToolCall(id, tool, JsonSerializer.Serialize(args))] };

    private static List<string> Turns(FakeLlmServerFixture llm) => llm.Queue.Requests.Where(OffersTools).ToList();

    private static async Task<string?> RunStatus(IServiceProvider services)
        => (await Repository(services, r => r.GetAsync(RunId)))?.Status;

    private static async Task<T> Repository<T>(IServiceProvider services, Func<IWorkflowRunRepository, Task<T>> read)
    {
        using var user = BackgroundUserContext.BeginScope(Owner);
        using var scope = services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<IWorkflowRunRepository>());
    }

    private static async Task<IHarnessSession> SpawnAsync(
        OpenCodeHarnessRuntime runtime, string sessionId, string workspace, Dictionary<string, string> environment, bool workflowStep, CancellationToken ct)
        => await runtime.SpawnAsync(
            new HarnessSpawnOptions
            {
                SessionId = sessionId,
                WorkingDirectory = workspace,
                OwnerUserId = Owner,
                LaunchArtifacts = new OpenCodeLaunchArtifacts(environment),
                WorkflowStep = workflowStep,
            },
            ct);

    /// <summary>Two sessions, and a one-step run whose step is running in the second.</summary>
    private static void Seed(IServiceProvider services, string workspace)
    {
        using var scope = services.CreateScope();
        using var connection = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO workspaces (id, directory, display_name, created_at, user_id)
            VALUES ('ws-live', '{workspace}', 'Live', '2026-09-23T00:00:00+00:00', '{Owner}');
            INSERT INTO instances (id, port, pid, directory, url, status, created_at, user_id)
            VALUES ('inst-live', 0, NULL, '{workspace}', '', 'running', '2026-09-23T00:00:00+00:00', '{Owner}');
            INSERT INTO sessions (
                id, workspace_id, instance_id, opencode_session_id, title, status, directory,
                lifecycle_status, retention_status, created_at, user_id, workflow_run_id)
            VALUES ('{Normal}', 'ws-live', 'inst-live', 'pending', 'Ordinary session', 'active', '{workspace}',
                    'running', 'active', '2026-09-23T00:00:00+00:00', '{Owner}', NULL),
                   ('{Step}', 'ws-live', 'inst-live', 'pending', 'Live check · Check', 'active', '{workspace}',
                    'running', 'active', '2026-09-23T00:00:00+00:00', '{Owner}', '{RunId}');
            INSERT INTO workflow_runs (
                id, user_id, workflow_id, workflow_name, definition, request, slug, title, repository_path,
                harness_type, options, status, current_step_id, created_at, updated_at)
            VALUES ('{RunId}', '{Owner}', 'repo:live', 'Live check', @definition, 'Check it', 'check-it', 'Check it',
                    '{workspace}', 'opencode', '{"{}"}', 'running', 'check', '2026-09-23T00:00:00Z', '2026-09-23T00:00:00Z');
            INSERT INTO workflow_run_steps (id, run_id, step_id, visit, session_id, status, started_at)
            VALUES ('visit-live', '{RunId}', 'check', 1, '{Step}', 'running', '2026-09-23T00:00:00Z');
            """;
        var definition = command.CreateParameter();
        definition.ParameterName = "@definition";
        definition.Value = Workflow;
        command.Parameters.Add(definition);
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
                VALUES ('{session.InstanceId}', 0, NULL, '', '', 'running', '2026-09-23T00:00:00+00:00', '{Owner}');
                UPDATE sessions SET instance_id = '{session.InstanceId}', harness_resume_token = {token} WHERE id = '{sessionId}';
                """;
            command.ExecuteNonQuery();
        }

        services.GetRequiredService<InstanceTracker>().Register(session.InstanceId, session);
    }

    internal static HashSet<string> OfferedToolNames(IEnumerable<string> requests)
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
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            var value = read();
            if (done(value))
                return value;

            try
            {
                await Task.Delay(200, wait.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            var value = await read();
            if (done(value))
                return value;

            try
            {
                await Task.Delay(200, wait.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException();
            }
        }
    }
}
