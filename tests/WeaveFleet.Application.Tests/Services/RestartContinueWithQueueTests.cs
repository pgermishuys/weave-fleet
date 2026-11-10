using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A session cut off by a restart with a message in its queue (found checking a competitor's fix, 9 Nov 2026): Continue
/// sends its own message, and the queued one still goes, once, when the continued turn ends.
/// </summary>
public sealed class RestartContinueWithQueueTests : IAsyncDisposable
{
    private readonly TestUserContext _user = new("user-1");
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _harness = new("inst-1");
    private readonly InMemoryQueuedPromptRepository _queue = new();

    public RestartContinueWithQueueTests() => _builder = new SessionOrchestratorBuilder().WithUserContext(_user);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task The_queued_message_goes_once_after_the_continued_turn_and_not_before()
    {
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsSteering = true });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active", Directory = "/tmp", CreatedAt = "2026-01-01",
            RetentionStatus = "active", HarnessType = "opencode", UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _harness);
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, "user-1");
        _builder.Build();

        var retries = new TurnRetryScheduler(TestServiceScopeFactory.CreateEmpty(), TimeProvider.System, NullLogger<TurnRetryScheduler>.Instance);
        var queueService = new PromptQueueService(
            _builder.Prompting, _builder.ShellCommands, _builder.SessionRepository, _queue, _builder.EventBroadcaster,
            _builder.ActivityTracker, _builder.HarnessRegistry, _user, retries, NullLogger<PromptQueueService>.Instance);
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Busy, "user-1");
        await queueService.EnqueueAsync("s1", new QueuePromptRequest("also run the linter", QueuedPromptKinds.Prompt));
        // Fleet restarted: the turn was cut off, the session is idle again, and the queue is still in the database.
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, "user-1");

        var interruptions = new MemoryInterruptions();
        await interruptions.SaveAsync(new SessionInterruption { SessionId = "s1", UserId = "user-1", At = DateTimeOffset.UtcNow });
        var continuer = new SessionInterruptions(
            new FleetOptions(), interruptions, _builder.SessionRepository, _builder.WorkspaceRepository, _builder.SessionMessageProxy,
            _builder.Prompting, _builder.EventBroadcaster, _user, new NoBackgroundUser(), new RestartContinuer(NullLogger<RestartContinuer>.Instance),
            TimeProvider.System, NullLogger<SessionInterruptions>.Instance);

        (await continuer.ContinueAsync("s1")).IsSuccess.ShouldBeTrue();

        // Continue went; the queued message waits for the continued turn to end.
        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe(["Continue where you left off."]);
        (await _queue.ListAsync("s1")).Select(i => i.Text).ShouldBe(["also run the linter"]);

        // The continued turn ends: the queued message goes, once.
        await queueService.SendNextAsync("s1");
        await queueService.SendNextAsync("s1");
        _harness.SendPromptCalls.Select(c => c.Text).ShouldBe(["Continue where you left off.", "also run the linter"], ignoreOrder: true);
        (await _queue.ListAsync("s1")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Continue_runs_at_the_effort_the_session_was_on()
    {
        _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsSteering = true });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1", InstanceId = "inst-1", Title = "T", Status = "active", Directory = "/tmp", CreatedAt = "2026-01-01",
            RetentionStatus = "active", HarnessType = "opencode", UserId = "user-1", SelectedEffort = "high",
        });
        _builder.InstanceTracker.Register("inst-1", _harness);
        _builder.ActivityTracker.Update("s1", ActivityStatuses.Idle, "user-1");
        _builder.Build();

        var interruptions = new MemoryInterruptions();
        await interruptions.SaveAsync(new SessionInterruption { SessionId = "s1", UserId = "user-1", At = DateTimeOffset.UtcNow });
        var continuer = new SessionInterruptions(
            new FleetOptions(), interruptions, _builder.SessionRepository, _builder.WorkspaceRepository, _builder.SessionMessageProxy,
            _builder.Prompting, _builder.EventBroadcaster, _user, new NoBackgroundUser(), new RestartContinuer(NullLogger<RestartContinuer>.Instance),
            TimeProvider.System, NullLogger<SessionInterruptions>.Instance);

        (await continuer.ContinueAsync("s1")).IsSuccess.ShouldBeTrue();

        _harness.SendPromptCalls.ShouldHaveSingleItem().Options.ShouldNotBeNull().Effort.ShouldBe("high");
    }

    private sealed class NoBackgroundUser : IBackgroundUserScope
    {
        public IDisposable Begin(string userId) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class MemoryInterruptions : ISessionInterruptionRepository
    {
        private readonly Dictionary<string, SessionInterruption> _items = [];

        public Task SaveAsync(SessionInterruption interruption) { _items[interruption.SessionId] = interruption; return Task.CompletedTask; }
        public Task<SessionInterruption?> GetAsync(string sessionId) => Task.FromResult(_items.GetValueOrDefault(sessionId));
        public Task<SessionInterruption?> RemoveAsync(string sessionId)
        {
            _items.Remove(sessionId, out var removed);
            return Task.FromResult(removed);
        }
        public Task FlagTurnAsync(string sessionId, DateTimeOffset startedAt, bool waiting) => throw new NotSupportedException();
        public Task SetWaitingAsync(string sessionId, bool waiting) => throw new NotSupportedException();
        public Task ClearTurnAsync(string sessionId) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlaggedTurn>> ListFlaggedAsync() => throw new NotSupportedException();
        public Task SetFailureAsync(string sessionId, DateTimeOffset at, string message) => throw new NotSupportedException();
        public Task<bool> ClearFailureAsync(string sessionId) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, SessionFailure>> ListFailuresForUserAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInterruption>> ListForUserAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInterruption>> ListAllAsync() => throw new NotSupportedException();
    }
}
