using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Tests.Harnesses;

public sealed class HarnessAvailabilityCacheTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
    private readonly ControlledRegistry _registry = new();
    private readonly HarnessAvailabilityCache _cache;

    public HarnessAvailabilityCacheTests()
    {
        _cache = new HarnessAvailabilityCache(_registry, _clock, NullLogger<HarnessAvailabilityCache>.Instance);
    }

    [Fact]
    public async Task The_first_read_waits_for_a_check_and_later_reads_get_its_answer_without_checking()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        first.IsCompleted.ShouldBeFalse();
        (await _registry.Answer("1.18.32")).ShouldBe(1);

        (await first).Harnesses.Single().Version.ShouldBe("1.18.32");
        (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version.ShouldBe("1.18.32");
        _registry.Checks.ShouldBe(1);
    }

    [Fact]
    public async Task Readers_that_arrive_while_the_first_check_runs_share_it()
    {
        var reads = Enumerable.Range(0, 5).Select(_ => _cache.GetAsync(fresh: false, CancellationToken.None)).ToList();
        await _registry.Answer("1.18.32");

        foreach (var read in reads)
            (await read).Harnesses.Single().Version.ShouldBe("1.18.32");
        _registry.Checks.ShouldBe(1);
    }

    [Fact]
    public async Task An_old_answer_is_served_at_once_and_checked_again_behind_it()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Answer("1.18.31");
        await first;

        _clock.Advance(HarnessAvailabilityCache.FreshFor);
        var old = _cache.GetAsync(fresh: false, CancellationToken.None);
        old.IsCompletedSuccessfully.ShouldBeTrue();
        (await old).Harnesses.Single().Version.ShouldBe("1.18.31");

        // One check behind it, however many reads come in meanwhile.
        (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version.ShouldBe("1.18.31");
        await _registry.Answer("1.18.32");
        _registry.Checks.ShouldBe(2);

        await WaitUntil(async () => (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version == "1.18.32");
        _registry.Checks.ShouldBe(2);
    }

    [Fact]
    public async Task A_fresh_read_waits_for_its_own_check_and_everyone_gets_the_new_answer()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Answer("1.18.31");
        await first;

        var fresh = _cache.GetAsync(fresh: true, CancellationToken.None);
        fresh.IsCompleted.ShouldBeFalse();
        // Others aren't held up by it.
        (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version.ShouldBe("1.18.31");

        await _registry.Answer("1.18.32");
        (await fresh).Harnesses.Single().Version.ShouldBe("1.18.32");
        (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task A_fresh_read_does_not_take_the_answer_of_a_check_that_started_before_it()
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Started(1);

        var fresh = _cache.GetAsync(fresh: true, CancellationToken.None);
        await _registry.Started(2);

        await _registry.Answer("not installed yet");
        (await first).Harnesses.Single().Version.ShouldBe("not installed yet");
        fresh.IsCompleted.ShouldBeFalse();

        await _registry.Answer("1.18.32");
        (await fresh).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task After_Forget_the_next_read_waits_for_a_new_check()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Answer("1.18.31");
        await first;

        _cache.Forget();
        var next = _cache.GetAsync(fresh: false, CancellationToken.None);
        next.IsCompleted.ShouldBeFalse();
        await _registry.Answer("1.18.32");
        (await next).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task An_older_check_that_finishes_last_does_not_replace_a_newer_answer()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Started(1);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var fresh = _cache.GetAsync(fresh: true, CancellationToken.None);
        await _registry.Started(2);

        await _registry.AnswerCheck(2, "1.18.32");
        await fresh;
        await _registry.AnswerCheck(1, "1.18.31");
        await first;

        (await _cache.GetAsync(fresh: false, CancellationToken.None)).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task A_failed_check_fails_its_readers_and_the_next_read_checks_again()
    {
        var first = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Fail(new InvalidOperationException("probe blew up"));
        await Should.ThrowAsync<InvalidOperationException>(() => first);

        var second = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Answer("1.18.32");
        (await second).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task A_reader_that_gives_up_leaves_the_check_running_for_the_others()
    {
        using var cancel = new CancellationTokenSource();
        var leaving = _cache.GetAsync(fresh: false, cancel.Token);
        var staying = _cache.GetAsync(fresh: false, CancellationToken.None);
        await cancel.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => leaving);

        await _registry.Answer("1.18.32");
        (await staying).Harnesses.Single().Version.ShouldBe("1.18.32");
    }

    [Fact]
    public async Task The_answer_says_when_its_check_started()
    {
        var read = _cache.GetAsync(fresh: false, CancellationToken.None);
        await _registry.Started(1);
        var startedAt = _clock.GetUtcNow();
        _clock.Advance(TimeSpan.FromSeconds(2));
        await _registry.Answer("1.18.32");

        (await read).CheckedAt.ShouldBe(startedAt);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The condition never held.");
    }

    /// <summary>A registry whose checks finish when the test says, in the order they started unless told otherwise.</summary>
    private sealed class ControlledRegistry : IHarnessRegistry
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource<string>> _checks = [];
        private readonly HashSet<int> _answered = [];

        public int Checks
        {
            get { lock (_gate) return _checks.Count; }
        }

        public IReadOnlyList<IHarness> GetAll() => [];
        public IHarness? GetByType(string harnessType) => null;
        public IHarnessRuntime? GetRuntimeByType(string harnessType) => null;

        public async Task<IReadOnlyList<HarnessInfo>> GetAvailabilityAsync(CancellationToken ct)
        {
            var check = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _checks.Add(check);
            var version = await check.Task;
            return
            [
                HarnessInfo.From("opencode", "OpenCode", new HarnessCapabilities(), HarnessAvailability.Ready(version, "/usr/bin/opencode")),
            ];
        }

        /// <summary>Waits until <paramref name="count"/> checks have started.</summary>
        public async Task Started(int count)
        {
            for (var i = 0; i < 500 && Checks < count; i++) await Task.Delay(5);
            Checks.ShouldBeGreaterThanOrEqualTo(count);
        }

        /// <summary>Answers the oldest unanswered check; returns how many checks have started.</summary>
        public async Task<int> Answer(string version)
        {
            await Started(_answered.Count + 1);
            int number;
            lock (_gate) number = Enumerable.Range(1, _checks.Count).First(n => !_answered.Contains(n));
            await AnswerCheck(number, version);
            return Checks;
        }

        public async Task AnswerCheck(int number, string version)
        {
            await Started(number);
            TaskCompletionSource<string> check;
            lock (_gate)
            {
                _answered.Add(number);
                check = _checks[number - 1];
            }
            check.SetResult(version);
            await Task.Yield();
        }

        public async Task Fail(Exception exception)
        {
            await Started(_answered.Count + 1);
            TaskCompletionSource<string> check;
            lock (_gate)
            {
                var number = Enumerable.Range(1, _checks.Count).First(n => !_answered.Contains(n));
                _answered.Add(number);
                check = _checks[number - 1];
            }
            check.SetException(exception);
        }
    }
}
