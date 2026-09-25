using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>A prompt the user sends into a running turn (steers) rather than after it.</summary>
public sealed class SteeringPromptTests : IAsyncDisposable
{
    private const string SessionId = "s1";
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _harnessSession = new("inst-1");

    public ValueTask DisposeAsync() => _harnessSession.DisposeAsync();

    private SessionOrchestrator Build(bool supportsSteering)
    {
        _builder.RegisterHarness("steerable", capabilities: new HarnessCapabilities { SupportsSteering = supportsSteering });
        _builder.SessionRepository.Seed(new Session
        {
            Id = SessionId,
            InstanceId = "inst-1",
            Title = "Test",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = "active",
            HarnessType = "steerable",
        });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _builder.InstanceTracker.Register("inst-1", _harnessSession);
        return _builder.Build();
    }

    private static PromptOptions Steer => new() { Delivery = PromptDelivery.Steer };

    private bool? BroadcastSteered()
    {
        var broadcast = _builder.EventBroadcaster.Broadcasts.Single(b => b.Topic == $"session:{SessionId}" && b.Type == "message.updated");
        return broadcast.Payload.GetProperty("info").TryGetProperty("steered", out var steered) ? steered.GetBoolean() : null;
    }

    [Theory]
    [InlineData(ActivityStatuses.Busy)]
    [InlineData(ActivityStatuses.Retry)]
    [InlineData(ActivityStatuses.Delegating)]
    public async Task steers_a_running_turn_and_shows_the_prompt_as_sent_mid_turn(string status)
    {
        var sut = Build(supportsSteering: true);
        _builder.ActivityTracker.Update(SessionId, status, "user-1");

        var result = await sut.PromptSessionAsync(SessionId, "stop, wrong file", Steer);

        result.IsSuccess.ShouldBeTrue();
        _harnessSession.SendPromptCalls.Single().Options!.Delivery.ShouldBe(PromptDelivery.Steer);
        BroadcastSteered().ShouldBe(true);
    }

    [Fact]
    public async Task a_steer_that_finds_the_turn_over_starts_a_turn_of_its_own()
    {
        var sut = Build(supportsSteering: true);
        _builder.ActivityTracker.Update(SessionId, ActivityStatuses.Idle, "user-1");

        var result = await sut.PromptSessionAsync(SessionId, "stop, wrong file", Steer);

        result.IsSuccess.ShouldBeTrue();
        _harnessSession.SendPromptCalls.Single().Options!.Delivery.ShouldBe(PromptDelivery.Queue);
        BroadcastSteered().ShouldBeNull();
    }

    [Fact]
    public async Task refuses_a_steer_the_harness_cant_take()
    {
        var sut = Build(supportsSteering: false);
        _builder.ActivityTracker.Update(SessionId, ActivityStatuses.Busy, "user-1");

        var result = await sut.PromptSessionAsync(SessionId, "stop, wrong file", Steer);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Prompt.Delivery");
        _harnessSession.SendPromptCalls.ShouldBeEmpty();
        _builder.EventBroadcaster.Broadcasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_queued_prompt_says_so_and_isnt_marked()
    {
        var sut = Build(supportsSteering: true);
        _builder.ActivityTracker.Update(SessionId, ActivityStatuses.Busy, "user-1");

        var result = await sut.PromptSessionAsync(SessionId, "after the turn", new PromptOptions { Delivery = PromptDelivery.Queue });

        result.IsSuccess.ShouldBeTrue();
        _harnessSession.SendPromptCalls.Single().Options!.Delivery.ShouldBe(PromptDelivery.Queue);
        BroadcastSteered().ShouldBeNull();
    }

    [Fact]
    public async Task a_prompt_that_doesnt_say_is_left_to_the_harness()
    {
        var sut = Build(supportsSteering: true);
        _builder.ActivityTracker.Update(SessionId, ActivityStatuses.Busy, "user-1");

        var result = await sut.PromptSessionAsync(SessionId, "from an automation");

        result.IsSuccess.ShouldBeTrue();
        _harnessSession.SendPromptCalls.Single().Options!.Delivery.ShouldBeNull();
    }
}
