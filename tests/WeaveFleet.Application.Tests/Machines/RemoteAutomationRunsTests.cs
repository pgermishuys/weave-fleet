using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Automations;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Machines;

/// <summary>
/// An automation that names another machine starts its run there, through that machine's API with the token kept for
/// it; one that names none runs here as it always has. A machine that doesn't answer means a skipped run that says so.
/// </summary>
public sealed class RemoteAutomationRunsTests : IAsyncDisposable
{
    private readonly FakeMachine _atlas = new();
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _session = new("inst-1");
    private readonly AutomationExecutionService _sut;

    public RemoteAutomationRunsTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        _builder.InstanceTracker.Register("inst-1", _session);
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "Mine", Status = "active", Directory = "/tmp",
            CreatedAt = "2026-01-01", RetentionStatus = "active", HarnessType = "opencode",
        });
        _builder.Build();
        _sut = new AutomationExecutionService(
            _builder.Creation,
            _builder.Prompting,
            _builder.SessionRepository,
            NullLogger<AutomationExecutionService>.Instance,
            _atlas.Runs);
    }

    public ValueTask DisposeAsync()
    {
        _atlas.Dispose();
        return _session.DisposeAsync();
    }

    private static Automation Automation(string targetType = "new_session", string? machine = FakeMachine.Id) => new()
    {
        Id = "a1", Name = "Nightly check", Prompt = "Check the build", TriggerType = "schedule", TriggerConfig = "0 2 * * *",
        TargetType = targetType, TargetMachineId = machine, WorkspaceId = "/home/me/source/harbor-api", Isolation = "worktree",
        BaseBranch = "origin/main", HarnessType = "opencode", Agent = "shuttle", Model = "github-copilot/claude-haiku-4.5",
        TimeZone = "Africa/Johannesburg", UserId = "user-1",
    };

    private static (HttpStatusCode, string) Ok(string json) => (HttpStatusCode.OK, json);

    [Fact]
    public async Task An_automation_without_a_machine_runs_here_as_before()
    {
        var outcome = await _sut.ExecuteAsync(Automation("same_session", machine: null), previousSessionId: "s1");

        outcome.SessionId.ShouldBe("s1");
        outcome.MachineId.ShouldBeNull();
        _session.SendPromptCalls.ShouldHaveSingleItem();
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_on_another_machine_starts_its_session_there_as_the_composer_would()
    {
        _atlas.Answer = request => request == "POST /api/sessions"
            ? Ok("""{"instanceId":"atlas-inst","workspaceId":"w1","session":{"id":"atlas-session"}}""")
            : null;

        var outcome = await _sut.ExecuteAsync(Automation());

        (outcome.SessionId, outcome.InstanceId, outcome.Error, outcome.Skipped).ShouldBe(("atlas-session", "atlas-inst", null, false));
        (outcome.MachineId, outcome.MachineName).ShouldBe((FakeMachine.Id, FakeMachine.Name));
        var (request, token, body) = _atlas.Requests.ShouldHaveSingleItem();
        (request, token).ShouldBe(("POST /api/sessions", FakeMachine.Token));

        var sent = JsonDocument.Parse(body!).RootElement;
        sent.GetProperty("title").GetString().ShouldBe("Automation: Nightly check");
        sent.GetProperty("initialPrompt").GetString().ShouldBe("Check the build");
        sent.GetProperty("directory").GetString().ShouldBe("/home/me/source/harbor-api");
        sent.GetProperty("isolationStrategy").GetString().ShouldBe("worktree");
        // The run's time on the automation's clock (UTC+2), as a run here names it.
        sent.GetProperty("branch").GetString().ShouldBe("fleet/auto-nightly-check-20261008-0930");
        sent.GetProperty("harnessType").GetString().ShouldBe("opencode");
        sent.GetProperty("agent").GetString().ShouldBe("shuttle");
        sent.GetProperty("model").GetProperty("providerID").GetString().ShouldBe("github-copilot");
        sent.GetProperty("model").GetProperty("modelID").GetString().ShouldBe("claude-haiku-4.5");
        var source = sent.GetProperty("source");
        source.GetProperty("key").GetProperty("providerId").GetString().ShouldBe("builtin.repository");
        source.GetProperty("input").GetProperty("repositoryPath").GetString().ShouldBe("/home/me/source/harbor-api");
        source.GetProperty("input").GetProperty("branch").GetString().ShouldBe("fleet/auto-nightly-check-20261008-0930");
        source.GetProperty("input").GetProperty("baseBranch").GetString().ShouldBe("origin/main");
    }

    [Fact]
    public async Task A_run_in_the_folder_as_it_is_starts_in_that_folder_there()
    {
        _atlas.Answer = _ => Ok("""{"instanceId":"i","session":{"id":"atlas-session"}}""");
        var automation = Automation();
        automation.Isolation = "existing";
        automation.BaseBranch = null;

        await _sut.ExecuteAsync(automation);

        var sent = JsonDocument.Parse(_atlas.Requests.ShouldHaveSingleItem().Body!).RootElement;
        sent.GetProperty("isolationStrategy").GetString().ShouldBe("existing");
        sent.TryGetProperty("branch", out _).ShouldBeFalse();
        sent.GetProperty("source").GetProperty("key").GetProperty("providerId").GetString().ShouldBe("builtin.local");
        sent.GetProperty("source").GetProperty("input").GetProperty("directory").GetString().ShouldBe("/home/me/source/harbor-api");
    }

    [Fact]
    public async Task A_machine_that_doesnt_answer_means_a_skipped_run_that_says_so()
    {
        _atlas.Away = true;

        var outcome = await _sut.ExecuteAsync(Automation());

        outcome.Skipped.ShouldBeTrue();
        outcome.Error.ShouldBe("Skipped: atlas didn't answer.");
        (outcome.MachineId, outcome.MachineName).ShouldBe((FakeMachine.Id, FakeMachine.Name));
        _atlas.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_machine_that_turns_the_token_away_means_a_skipped_run()
    {
        _atlas.Answer = _ => (HttpStatusCode.Unauthorized, "");

        var outcome = await _sut.ExecuteAsync(Automation());

        outcome.Skipped.ShouldBeTrue();
        outcome.Error.ShouldBe("Skipped: atlas turned this Fleet's token away. Update it in Settings › Machines.");
    }

    [Fact]
    public async Task A_machine_no_longer_in_the_list_means_a_skipped_run()
    {
        await _atlas.Machines.DeleteAsync(FakeMachine.Id);

        var outcome = await _sut.ExecuteAsync(Automation());

        outcome.Skipped.ShouldBeTrue();
        outcome.Error.ShouldBe("Skipped: the machine it runs on isn't in this Fleet's list any more.");
        _atlas.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_the_machine_refuses_fails_with_its_reason()
    {
        _atlas.Answer = _ => (HttpStatusCode.BadRequest, """{"error":"/home/me/source/harbor-api isn't a git repository."}""");

        var outcome = await _sut.ExecuteAsync(Automation());

        outcome.Skipped.ShouldBeFalse();
        outcome.SessionId.ShouldBeNull();
        outcome.Error.ShouldBe("Couldn't start: atlas: /home/me/source/harbor-api isn't a git repository.");
    }

    [Fact]
    public async Task The_same_session_each_run_prompts_the_last_runs_session_there()
    {
        _atlas.Answer = request => request switch
        {
            "GET /api/sessions/atlas-session" => Ok("""{"id":"atlas-session","retentionStatus":"active","harnessType":"opencode"}"""),
            "POST /api/sessions/atlas-session/prompt" => Ok("""{"eventId":"e1"}"""),
            _ => null,
        };

        var outcome = await _sut.ExecuteAsync(Automation("same_session"), previousSessionId: "atlas-session");

        outcome.SessionId.ShouldBe("atlas-session");
        outcome.MachineName.ShouldBe(FakeMachine.Name);
        _atlas.Requests.Select(r => r.Request).ShouldBe(["GET /api/sessions/atlas-session", "POST /api/sessions/atlas-session/prompt"]);
        // The session answers with its own agent and model: through the API, a prompt's would stay with it.
        JsonDocument.Parse(_atlas.Requests[1].Body!).RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["text"]);
        _session.SendPromptCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_session_each_run_starts_a_new_one_when_the_machine_no_longer_has_it()
    {
        _atlas.Answer = request => request switch
        {
            "POST /api/sessions" => Ok("""{"instanceId":"i","session":{"id":"atlas-new"}}"""),
            _ => null,
        };

        var outcome = await _sut.ExecuteAsync(Automation("same_session"), previousSessionId: "atlas-gone");

        outcome.SessionId.ShouldBe("atlas-new");
        _atlas.Requests.Select(r => r.Request).ShouldBe(["GET /api/sessions/atlas-gone", "POST /api/sessions"]);
    }

    [Fact]
    public async Task A_workflow_target_starts_the_workflow_run_there()
    {
        _atlas.Answer = request => request == "POST /api/workflows/runs"
            ? Ok("""{"id":"wf-run-1","status":"running","sessions":[{"sessionId":"step-1"}]}""")
            : null;
        var automation = Automation("workflow");
        automation.WorkflowId = "builtin:fix-a-bug";
        automation.WorkflowSteps = ["review"];

        var outcome = await _sut.ExecuteAsync(automation);

        (outcome.WorkflowRunId, outcome.SessionId, outcome.MachineName).ShouldBe(("wf-run-1", "step-1", FakeMachine.Name));
        var sent = JsonDocument.Parse(_atlas.Requests.ShouldHaveSingleItem().Body!).RootElement;
        sent.GetProperty("workflowId").GetString().ShouldBe("builtin:fix-a-bug");
        sent.GetProperty("directory").GetString().ShouldBe("/home/me/source/harbor-api");
        sent.GetProperty("request").GetString().ShouldBe("Check the build");
        sent.GetProperty("checkWithMe").GetBoolean().ShouldBeFalse();
        sent.GetProperty("optionalSteps").EnumerateArray().Select(s => s.GetString()).ShouldBe(["review"]);
    }

    [Fact]
    public async Task A_workflow_start_the_machine_refuses_is_skipped_with_its_reason()
    {
        _atlas.Answer = _ => (HttpStatusCode.Conflict, """{"error":"Workflows are turned off in Settings."}""");

        var outcome = await _sut.ExecuteAsync(Automation("workflow"));

        outcome.Skipped.ShouldBeTrue();
        outcome.Error.ShouldBe("Skipped: atlas: Workflows are turned off in Settings.");
    }
}
