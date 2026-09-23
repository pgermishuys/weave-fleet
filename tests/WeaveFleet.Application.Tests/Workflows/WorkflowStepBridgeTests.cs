using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Workflows;

public sealed class WorkflowStepBridgeTests
{
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeResolver _resolver = new();
    private readonly WorkflowStepBridge _bridge;

    public WorkflowStepBridgeTests()
    {
        _preferences.Seed(FleetWorkflows.PreferenceKey, "true");
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IWorkflowRunRepository>(new InMemoryWorkflowRunRepository());
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
        });
        var runner = new WorkflowRunner(scopes, new SessionActivityTracker(), TimeProvider.System, NullLogger<WorkflowRunner>.Instance);
        _bridge = new WorkflowStepBridge([_resolver], new NoUserScope(), new WorkflowsFeature(new FleetOptions(), _preferences), runner);
    }

    [Fact]
    public async Task a_subagent_of_a_step_session_cant_finish_the_step()
    {
        _resolver.Caller = new HarnessCanvasCaller("step-session", "u1", ViaParent: true);

        var result = await _bridge.DoneAsync("token", "child-harness-session", "pass", "…");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(CanvasErrorKind.Refused);
        result.Error.Message.ShouldBe(WorkflowStepBridge.NotAStepMessage);
    }

    [Fact]
    public async Task a_session_that_isnt_a_step_cant_finish_one()
    {
        _resolver.Caller = new HarnessCanvasCaller("ordinary-session", "u1");

        var result = await _bridge.DoneAsync("token", "harness-session", "pass", "…");

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Message.ShouldBe(WorkflowStepBridge.NotAStepMessage);
    }

    [Fact]
    public async Task nothing_is_accepted_while_workflows_are_off()
    {
        _preferences.Seed(FleetWorkflows.PreferenceKey, "false");
        _resolver.Caller = new HarnessCanvasCaller("step-session", "u1");

        var result = await _bridge.DoneAsync("token", "harness-session", "pass", "…");

        result.Error!.Message.ShouldBe(FleetWorkflows.TurnedOffMessage);
    }

    [Fact]
    public async Task an_unknown_caller_is_not_found()
    {
        var result = await _bridge.DoneAsync("token", "harness-session", "pass", "…");

        result.Error!.Kind.ShouldBe(CanvasErrorKind.NotFound);
    }

    private sealed class FakeResolver : IHarnessCanvasCallerResolver
    {
        public HarnessCanvasCaller? Caller { get; set; }

        public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
            => Task.FromResult(Caller);
    }

    private sealed class NoUserScope : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

public sealed class WorkflowModelRolesTests
{
    private static readonly WorkflowAgentStep Review = new(
        "review", "Review", 1, "build", WorkflowRoles.Strong, null, null, false, null, "Review it.", ["pass"], new Dictionary<string, string>(), null);

    private static readonly Dictionary<string, WorkflowModelChoice> Mapped = new()
    {
        [WorkflowRoles.Strong] = new("copilot/opus-5.5", "high"),
    };

    [Fact]
    public void a_role_uses_the_model_mapped_in_settings()
        => WorkflowModelRoles.Resolve(Review, Mapped, null).ShouldBe(new WorkflowModelChoice("copilot/opus-5.5", "high"));

    [Fact]
    public void the_runs_models_menu_wins_over_settings()
        => WorkflowModelRoles.Resolve(Review, Mapped, new Dictionary<string, WorkflowModelChoice> { [WorkflowRoles.Strong] = new("copilot/sonnet-5", null) })
            .ShouldBe(new WorkflowModelChoice("copilot/sonnet-5", null));

    [Fact]
    public void an_unmapped_role_uses_the_default_model()
        => WorkflowModelRoles.Resolve(Review, new Dictionary<string, WorkflowModelChoice>(), null).ShouldBe(WorkflowModelChoice.Default);

    [Fact]
    public void a_pinned_model_is_used_as_it_is()
        => WorkflowModelRoles.Resolve(Review with { Model = "copilot/gpt-5.4-mini", Effort = "low" }, Mapped, null)
            .ShouldBe(new WorkflowModelChoice("copilot/gpt-5.4-mini", "low"));

    [Fact]
    public void a_steps_effort_wins_over_the_roles()
        => WorkflowModelRoles.Resolve(Review with { Effort = "max" }, Mapped, null).ShouldBe(new WorkflowModelChoice("copilot/opus-5.5", "max"));

    [Fact]
    public void the_preference_reads_per_harness()
    {
        var all = WorkflowModelRoles.Read("""{"opencode":{"strong":{"model":"copilot/opus-5.5","effort":"high"}},"opencode2":{}}""");

        all["opencode"]["strong"].ShouldBe(new WorkflowModelChoice("copilot/opus-5.5", "high"));
        all["opencode2"].ShouldBeEmpty();
        WorkflowModelRoles.Read("not json").ShouldBeEmpty();
    }
}
