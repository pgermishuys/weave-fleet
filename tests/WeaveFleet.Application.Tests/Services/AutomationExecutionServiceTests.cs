using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class AutomationExecutionServiceTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _session = new("inst-1");
    private readonly AutomationExecutionService _sut;

    public AutomationExecutionServiceTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        _builder.InstanceTracker.Register("inst-1", _session);
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "Mine", Status = "active", Directory = "/tmp",
            CreatedAt = "2026-01-01", RetentionStatus = "active", HarnessType = "opencode",
            SelectedAgent = "loom", SelectedProviderId = "github-copilot", SelectedModelId = "claude-opus-4.7",
        });
        _sut = new AutomationExecutionService(
            _builder.Build(),
            _builder.SessionRepository,
            NullLogger<AutomationExecutionService>.Instance);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private static Automation SameSession(string? model = null, string? agent = null, string? harnessType = null) => new()
    {
        Id = "a1", Name = "Nightly check", Prompt = "Check the build", TriggerType = "schedule",
        TriggerConfig = "0 2 * * *", TargetType = "same_session",
        Model = model, Agent = agent, HarnessType = harnessType,
    };

    [Fact]
    public async Task A_run_on_a_session_uses_the_automations_agent_and_model_for_that_prompt_only()
    {
        var outcome = await _sut.ExecuteAsync(
            SameSession(model: "github-copilot/claude-haiku-4.5", agent: "shuttle", harnessType: "opencode"),
            previousSessionId: "s1");

        outcome.SessionId.ShouldBe("s1");
        var options = _session.SendPromptCalls.ShouldHaveSingleItem().Options.ShouldNotBeNull();
        options.Agent.ShouldBe("shuttle");
        options.ProviderId.ShouldBe("github-copilot");
        options.ModelId.ShouldBe("claude-haiku-4.5");

        var session = (await _builder.SessionRepository.GetByIdAsync("s1")).ShouldNotBeNull();
        session.SelectedAgent.ShouldBe("loom");
        session.SelectedModelId.ShouldBe("claude-opus-4.7");
    }

    [Fact]
    public async Task A_run_without_an_agent_or_model_gets_the_sessions_own()
    {
        await _sut.ExecuteAsync(SameSession(), previousSessionId: "s1");

        var options = _session.SendPromptCalls.ShouldHaveSingleItem().Options.ShouldNotBeNull();
        options.Agent.ShouldBe("loom");
        options.ProviderId.ShouldBe("github-copilot");
        options.ModelId.ShouldBe("claude-opus-4.7");
    }

    [Fact]
    public async Task A_run_on_a_session_of_another_harness_leaves_the_sessions_choices_alone()
    {
        await _sut.ExecuteAsync(
            SameSession(model: "anthropic/claude-haiku-4-5", agent: "reviewer", harnessType: "claude-code"),
            previousSessionId: "s1");

        var options = _session.SendPromptCalls.ShouldHaveSingleItem().Options.ShouldNotBeNull();
        options.Agent.ShouldBe("loom");
        options.ModelId.ShouldBe("claude-opus-4.7");
    }

    [Theory]
    [InlineData("openrouter/anthropic/claude-haiku-4.5", "openrouter", "anthropic/claude-haiku-4.5")]
    [InlineData("anthropic/claude-haiku-4-5", "anthropic", "claude-haiku-4-5")]
    [InlineData("claude-haiku-4-5", null, null)]
    [InlineData(null, null, null)]
    public void SplitModel_splits_at_the_first_slash(string? model, string? providerId, string? modelId)
    {
        AutomationExecutionService.SplitModel(model).ShouldBe((providerId, modelId));
    }
}
