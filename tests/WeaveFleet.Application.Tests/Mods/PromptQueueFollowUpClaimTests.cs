using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Mods;
using WeaveFleet.Application.Mods.Host;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Tests.Mods.Host;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Mods;

/// <summary>
/// A session that idles with a message queued is about to start its next turn: the dispatcher claims it as following up
/// so the relay doesn't tell the user it finished, and hands the idle back when no turn starts.
/// </summary>
public sealed class PromptQueueFollowUpClaimTests : IAsyncDisposable
{
    private const string User = "user-1";

    private readonly TestUserContext _user = new(User);
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly InMemoryQueuedPromptRepository _queue = new();
    private readonly PromptQueueService _service;
    private readonly PromptQueueDispatcher _dispatcher;

    public PromptQueueFollowUpClaimTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(_user);
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities());
        Seed("s1", "/work/demo");
        _builder.InstanceTracker.Register("inst-1", _harness);
        var orchestrator = _builder.Build();
        _service = new PromptQueueService(
            _builder.Prompting, _builder.ShellCommands, _builder.SessionRepository, _queue, _builder.EventBroadcaster,
            _builder.ActivityTracker, _builder.HarnessRegistry, _user,
            new TurnRetryScheduler(TestServiceScopeFactory.CreateEmpty(), TimeProvider.System, NullLogger<TurnRetryScheduler>.Instance),
            NullLogger<PromptQueueService>.Instance);
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IBackgroundUserScope>(new NoUserScope());
            services.AddSingleton(orchestrator);
            services.AddSingleton<ISessionRepository>(_builder.SessionRepository);
            services.AddSingleton(_service);
        });
        _dispatcher = new PromptQueueDispatcher(scopes, NullLogger<PromptQueueDispatcher>.Instance);
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, User);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private void Seed(string id, string directory, string retention = "active", string? sideOf = null, string? parent = null)
        => _builder.SessionRepository.Seed(new Session
        {
            Id = id, InstanceId = "inst-1", Title = "T", Status = "active", Directory = directory, CreatedAt = "2026-01-01",
            RetentionStatus = retention, HarnessType = "opencode", UserId = User, SideOfSessionId = sideOf, ParentSessionId = parent,
        });

    private static SessionIdled Idled() => new() { Payload = new SessionIdledPayload { SessionId = "s1" } };

    private Task<WeaveFleet.Domain.Common.Result<QueuedPrompt>> QueueAsync(string text) => _service.EnqueueWithoutSendingAsync("s1", new QueuePromptRequest(text, QueuedPromptKinds.Prompt));

    [Fact]
    public async Task An_idle_with_a_message_queued_is_claimed_and_the_message_is_sent_without_releasing_the_idle()
    {
        await QueueAsync("next");
        var released = 0;

        (await _dispatcher.ClaimFollowUpAsync("s1", User, Idled(), () => { released++; return Task.CompletedTask; })).ShouldBeTrue();
        _dispatcher.Observe("s1", User, Idled());
        await _dispatcher.Pending.Within();

        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe(["next"]);
        released.ShouldBe(0);
    }

    [Fact]
    public async Task An_idle_with_nothing_queued_is_not_claimed()
    {
        (await _dispatcher.ClaimFollowUpAsync("s1", User, Idled(), () => Task.CompletedTask)).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_an_idle_can_be_claimed()
    {
        await QueueAsync("next");
        var busy = new TurnEnded { Payload = new TurnEndedPayload { SessionId = "s1", MessageId = "m", Index = 0 } };

        (await _dispatcher.ClaimFollowUpAsync("s1", User, busy, () => Task.CompletedTask)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_follow_up_the_session_refuses_releases_the_idle_it_held_back()
    {
        await QueueAsync("next");
        var released = 0;
        (await _dispatcher.ClaimFollowUpAsync("s1", User, Idled(), () => { released++; return Task.CompletedTask; })).ShouldBeTrue();
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active", Directory = "/work/demo", CreatedAt = "2026-01-01",
            RetentionStatus = "archived", HarnessType = "opencode", UserId = User,
        });

        _dispatcher.Observe("s1", User, Idled());
        await _dispatcher.Pending.Within();

        _harness.SendPromptCalls.ShouldBeEmpty();
        released.ShouldBe(1);
    }
}
