using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// The messages the user queues while the agent works are Fleet's, not the browser's: they wait in the database and
/// go out one per turn when the session's turn ends, and every change is broadcast so each open client shows it.
/// </summary>
public sealed class PromptQueueServiceTests : IAsyncDisposable
{
    private readonly TestUserContext _user = new("user-1");
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly InMemoryQueuedPromptRepository _queue = new();

    public PromptQueueServiceTests() => _builder = new SessionOrchestratorBuilder().WithUserContext(_user);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private void Seed(string retentionStatus = "active", bool steering = true, bool shell = true)
    {
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsSteering = steering, SupportsShellCommands = shell });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            InstanceId = "inst-1",
            Title = "T",
            Status = "active",
            Directory = "/tmp",
            CreatedAt = "2026-01-01",
            RetentionStatus = retentionStatus,
            HarnessType = "opencode",
            UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _harness);
    }

    private void Busy() => _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, "user-1");

    private void Idle() => _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, "user-1");

    private PromptQueueService Build() => new(
        _builder.Build(),
        _builder.SessionRepository,
        _queue,
        _builder.EventBroadcaster,
        _builder.ActivityTracker,
        _builder.HarnessRegistry,
        _user,
        NullLogger<PromptQueueService>.Instance);

    private static async Task<string> QueueAsync(PromptQueueService service, string text, string kind = QueuedPromptKinds.Prompt, string? command = null)
        => (await service.EnqueueAsync("s1", new QueuePromptRequest(text, kind, Command: command, Agent: "reviewer", ProviderId: "p", ModelId: "m", Effort: "high"))).Value.Id;

    private List<string> LastBroadcastTexts()
    {
        var last = _builder.EventBroadcaster.Broadcasts.Last(b => b.Type == PromptQueueService.ChangedEvent);
        last.Topic.ShouldBe("session:s1");
        last.UserId.ShouldBe("user-1");
        return last.Payload.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToList();
    }

    [Fact]
    public async Task A_message_queued_while_the_agent_works_waits_and_every_client_hears_of_it()
    {
        Seed();
        Busy();
        var service = Build();

        await QueueAsync(service, "also check the tests");

        _harness.SendPromptCalls.ShouldBeEmpty();
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["also check the tests"]);
        LastBroadcastTexts().ShouldBe(["also check the tests"]);
    }

    [Fact]
    public async Task A_message_queued_after_the_turn_ended_goes_at_once()
    {
        Seed();
        Idle();

        await QueueAsync(Build(), "hello");

        _harness.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe("hello");
        (await _queue.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task When_the_turn_ends_only_the_first_goes_with_what_was_picked_when_it_was_queued()
    {
        Seed();
        Busy();
        var service = Build();
        await QueueAsync(service, "first");
        await QueueAsync(service, "second");

        await service.SendNextAsync("s1");

        var call = _harness.SendPromptCalls.ShouldHaveSingleItem();
        call.Text.ShouldBe("first");
        call.Options.ShouldNotBeNull().Agent.ShouldBe("reviewer");
        call.Options.Effort.ShouldBe("high");
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["second"]);
        LastBroadcastTexts().ShouldBe(["second"]);
    }

    [Fact]
    public async Task A_shell_command_is_no_turn_so_the_message_after_it_goes_too()
    {
        Seed();
        Busy();
        var service = Build();
        await QueueAsync(service, "!git status", QueuedPromptKinds.Shell);
        await QueueAsync(service, "what changed?");
        await QueueAsync(service, "and then?");

        await service.SendNextAsync("s1");

        _harness.ShellCommandCalls.ShouldHaveSingleItem().Command.ShouldBe("git status");
        _harness.SendPromptCalls.ShouldHaveSingleItem().Text.ShouldBe("what changed?");
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["and then?"]);
    }

    [Fact]
    public async Task A_queued_slash_command_runs_as_a_command()
    {
        Seed();
        Busy();
        var service = Build();
        await service.EnqueueAsync("s1", new QueuePromptRequest("/review src", QueuedPromptKinds.Command, Command: "review", Arguments: "src"));

        await service.SendNextAsync("s1");

        var call = _harness.SendCommandCalls.ShouldHaveSingleItem();
        call.Command.ShouldBe("review");
        call.Arguments.ShouldBe("src");
    }

    [Fact]
    public async Task Send_now_while_the_agent_works_steers_the_message_into_the_turn()
    {
        Seed(steering: true);
        Busy();
        var service = Build();
        var id = await QueueAsync(service, "stop, wrong file");

        var result = await service.SendNowAsync("s1", id);

        result.IsSuccess.ShouldBeTrue();
        var call = _harness.SendPromptCalls.ShouldHaveSingleItem();
        call.Text.ShouldBe("stop, wrong file");
        call.Options.ShouldNotBeNull().Delivery.ShouldBe(PromptDelivery.Steer);
        (await _queue.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_now_keeps_a_command_waiting_for_the_turn_to_end()
    {
        Seed();
        Busy();
        var service = Build();
        var queued = await service.EnqueueAsync("s1", new QueuePromptRequest("/review", QueuedPromptKinds.Command, Command: "review"));

        var result = await service.SendNowAsync("s1", queued.Value.Id);

        result.IsFailure.ShouldBeTrue();
        _harness.SendCommandCalls.ShouldBeEmpty();
        (await _queue.ListAsync("s1")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Send_now_on_a_harness_that_cant_steer_leaves_the_message_queued()
    {
        Seed(steering: false);
        Busy();
        var service = Build();
        var id = await QueueAsync(service, "later");

        var result = await service.SendNowAsync("s1", id);

        result.IsFailure.ShouldBeTrue();
        _harness.SendPromptCalls.ShouldBeEmpty();
        (await _queue.ListAsync("s1")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Send_now_on_an_idle_session_sends_it_as_usual()
    {
        Seed();
        Busy();
        var service = Build();
        var id = await QueueAsync(service, "left over");
        Idle();

        (await service.SendNowAsync("s1", id)).IsSuccess.ShouldBeTrue();

        _harness.SendPromptCalls.ShouldHaveSingleItem().Options.ShouldNotBeNull().Delivery.ShouldBe(PromptDelivery.Queue);
    }

    [Fact]
    public async Task A_removed_message_is_gone_for_every_client()
    {
        Seed();
        Busy();
        var service = Build();
        var id = await QueueAsync(service, "never mind");

        (await service.RemoveAsync("s1", id)).IsSuccess.ShouldBeTrue();
        (await service.RemoveAsync("s1", id)).IsFailure.ShouldBeTrue();

        (await _queue.ListAsync("s1")).ShouldBeEmpty();
        LastBroadcastTexts().ShouldBeEmpty();
    }

    [Fact]
    public async Task Nothing_is_queued_for_an_archived_session_or_a_shell_command_the_harness_cant_run()
    {
        Seed(retentionStatus: "archived", shell: false);
        Busy();
        var service = Build();

        (await service.EnqueueAsync("s1", new QueuePromptRequest("hi", QueuedPromptKinds.Prompt))).IsFailure.ShouldBeTrue();
        (await service.EnqueueAsync("s1", new QueuePromptRequest("!ls", QueuedPromptKinds.Shell))).IsFailure.ShouldBeTrue();
        (await service.EnqueueAsync("s1", new QueuePromptRequest("hi", "later"))).IsFailure.ShouldBeTrue();

        (await _queue.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_message_the_session_refuses_goes_back_to_the_front()
    {
        Seed();
        Busy();
        var service = Build();
        await QueueAsync(service, "first");
        await QueueAsync(service, "second");
        // Archived while the messages waited: the prompt is refused.
        (await _builder.SessionRepository.GetByIdAsync("s1"))!.RetentionStatus = "archived";

        await service.SendNextAsync("s1");

        _harness.SendPromptCalls.ShouldBeEmpty();
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["first", "second"]);
    }
}
