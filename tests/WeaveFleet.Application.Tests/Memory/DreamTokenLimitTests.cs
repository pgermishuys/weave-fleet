using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using WeaveFleet.Application.Events;
using WeaveFleet.Application.Memory.Dreaming;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Users;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Memory;

/// <summary>
/// A night's dream is stopped by the model call that reaches what's left of the night's budget, whichever harness runs
/// it: every harness reports each call's tokens to <see cref="SessionContextRecorder"/>, which tells <see cref="DreamFinisher"/>.
/// </summary>
public sealed class DreamTokenLimitTests
{
    private const string Owner = "owner";
    private readonly DreamSessions _running = new();
    private readonly FakeStopper _stopper = new();
    private readonly SessionContextRecorder _recorder;

    public DreamTokenLimitTests()
    {
        var scopes = TestServiceScopeFactory.Create(services =>
        {
            services.AddSingleton<IBackgroundUserScope, DreamingAutomationTests.NoUserScope>();
            services.AddSingleton<IUserContext>(new TestUserContext(Owner));
            services.AddSingleton<ISessionContextRepository>(new InMemorySessionContextRepository());
            services.AddSingleton<IEventBroadcaster>(new FakeEventBroadcaster());
            services.AddScoped<SessionContextService>();
            services.AddSingleton<IDreamStopper>(_stopper);
        });
        var finisher = new DreamFinisher(_running, scopes, NullLogger<DreamFinisher>.Instance);
        _recorder = new SessionContextRecorder(scopes, NullLogger<SessionContextRecorder>.Instance, finisher);
    }

    /// <summary>One model call: 2,000 fresh input, 30,000 read from the cache (they count, as in the session's total), 1,000 out.</summary>
    private static ContextUsageReport Call() => new() { Call = new ContextCall { Input = 2_000, CacheRead = 30_000, Output = 1_000 } };

    [Fact]
    public async Task A_dream_is_stopped_by_the_call_that_reaches_its_limit_and_only_once()
    {
        _running.Register("dreaming-1", "dream-1", Owner, tokenLimit: 100_000);

        for (var call = 1; call <= 3; call++)
            _recorder.Observe("dreaming-1", Owner, Call());
        await Task.Delay(50);
        _stopper.Stopped.ShouldBeEmpty();
        _running.Spent("dreaming-1").ShouldBe(99_000);

        _recorder.Observe("dreaming-1", Owner, Call()); // 132,000: one call past the limit, and no more
        _recorder.Observe("dreaming-1", Owner, Call()); // a call already under way when it was stopped
        (await _stopper.NextAsync()).ShouldBe(("dreaming-1", "dream-1"));
        await Task.Delay(50);
        _stopper.Stopped.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dream_with_no_limit_and_any_other_session_are_never_stopped()
    {
        _running.Register("dreaming-now", "dream-2", Owner);

        for (var call = 0; call < 20; call++)
        {
            _recorder.Observe("dreaming-now", Owner, Call());
            _recorder.Observe("someone-elses-session", Owner, Call());
        }

        await Task.Delay(50);
        _stopper.Stopped.ShouldBeEmpty();
    }

    [Fact]
    public void A_call_that_reports_only_the_models_limits_spends_nothing()
    {
        _running.Register("dreaming-1", "dream-1", Owner, tokenLimit: 10);

        _recorder.Observe("dreaming-1", Owner, new ContextUsageReport { Limit = 200_000 });

        _running.Spent("dreaming-1").ShouldBe(0);
    }

    private sealed class FakeStopper : IDreamStopper
    {
        private readonly TaskCompletionSource<(string, string)> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<(string SessionId, string DreamId)> Stopped { get; } = new();

        public Task StopForBudgetAsync(string sessionId, string dreamId)
        {
            Stopped.Enqueue((sessionId, dreamId));
            _first.TrySetResult((sessionId, dreamId));
            return Task.CompletedTask;
        }

        public Task<(string, string)> NextAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
