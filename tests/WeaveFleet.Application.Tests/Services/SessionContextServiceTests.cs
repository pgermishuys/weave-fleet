using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionContextServiceTests
{
    private const string Session = "session-1";

    private readonly InMemorySessionContextRepository _repository = new();
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero));
    private readonly SessionContextService _sut;

    public SessionContextServiceTests()
    {
        _sut = new SessionContextService(_repository, new TestUserContext(), _broadcaster, _time);
    }

    private static ContextCall Call(int input, int cacheRead = 0, int output = 100) => new()
    {
        Input = input,
        CacheRead = cacheRead,
        CacheWrite = 50,
        Output = output,
        Reasoning = 10,
    };

    private static ContextUsageReport Usage(ContextCall? call, int? limit = 200_000, string model = "claude-opus-5") => new()
    {
        Call = call,
        Limit = limit,
        CompactsAt = limit - 33_000,
        ModelId = model,
        ProviderId = "anthropic",
    };

    /// <summary>Ends a turn a few seconds on; the next call comes a second after it.</summary>
    private async Task EndTurnAsync()
    {
        _time.Advance(TimeSpan.FromSeconds(5));
        await _sut.RecordTurnEndedAsync(Session);
        _time.Advance(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task a_call_sets_how_full_the_context_is_and_tells_the_sessions_clients()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(input: 1_000, cacheRead: 60_000)));

        var context = (await _sut.GetAsync(Session)).ShouldNotBeNull();
        context.Used.ShouldBe(61_160);
        context.Limit.ShouldBe(200_000);
        context.CompactsAt.ShouldBe(167_000);
        context.LastCall.ShouldNotBeNull().CacheRead.ShouldBe(60_000);
        context.LastCallAt.ShouldBe(_time.GetUtcNow());

        var broadcast = _broadcaster.Broadcasts.ShouldHaveSingleItem();
        broadcast.Topic.ShouldBe($"session:{Session}");
        broadcast.Type.ShouldBe(SessionContextUsage.UpdatedEventType);
        broadcast.UserId.ShouldBe(TestUserContext.DefaultUserId);
        broadcast.Payload.GetProperty("used").GetInt32().ShouldBe(61_160);
        broadcast.Payload.GetProperty("limit").GetInt32().ShouldBe(200_000);
        broadcast.Payload.GetProperty("lastCall").GetProperty("cacheRead").GetInt32().ShouldBe(60_000);
    }

    [Fact]
    public async Task limits_are_kept_while_the_model_stays_and_dropped_when_it_changes()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(1_000)));
        await _sut.RecordUsageAsync(Session, Usage(Call(2_000), limit: null));
        (await _sut.GetAsync(Session))!.Limit.ShouldBe(200_000);

        await _sut.RecordUsageAsync(Session, Usage(Call(3_000), limit: null, model: "gpt-6"));
        var context = (await _sut.GetAsync(Session))!;
        context.ModelId.ShouldBe("gpt-6");
        context.Limit.ShouldBeNull();
        context.CompactsAt.ShouldBeNull();
    }

    [Fact]
    public async Task a_report_with_only_limits_keeps_the_last_call()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(1_000), limit: null));
        await _sut.RecordUsageAsync(Session, Usage(call: null, limit: 1_000_000));

        var context = (await _sut.GetAsync(Session))!;
        context.Used.ShouldBe(1_160);
        context.Limit.ShouldBe(1_000_000);
    }

    [Fact]
    public async Task the_same_report_twice_is_stored_and_sent_once()
    {
        await _sut.RecordUsageAsync(Session, Usage(call: null));
        await _sut.RecordUsageAsync(Session, Usage(call: null));

        _broadcaster.Broadcasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task each_turns_end_adds_the_size_after_its_last_call()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(1_000)));
        await _sut.RecordUsageAsync(Session, Usage(Call(5_000)));
        await EndTurnAsync();
        await _sut.RecordUsageAsync(Session, Usage(Call(9_000)));
        await EndTurnAsync();

        var turns = (await _sut.GetAsync(Session))!.Turns;
        turns.Select(turn => turn.Used).ShouldBe([5_160, 9_160]);
        turns.ShouldAllBe(turn => turn.Limit == 200_000 && !turn.AfterCompaction);
    }

    [Fact]
    public async Task a_turn_without_a_call_since_the_last_adds_nothing()
    {
        await EndTurnAsync();
        (await _sut.GetAsync(Session)).ShouldBeNull();

        await _sut.RecordUsageAsync(Session, Usage(Call(1_000)));
        await EndTurnAsync();
        await EndTurnAsync();

        (await _sut.GetAsync(Session))!.Turns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task only_the_most_recent_turns_are_kept()
    {
        for (var i = 1; i <= Domain.Entities.SessionContext.MaxTurns + 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await _sut.RecordUsageAsync(Session, Usage(Call(i * 100)));
            await EndTurnAsync();
        }

        var turns = (await _sut.GetAsync(Session))!.Turns;
        turns.Count.ShouldBe(Domain.Entities.SessionContext.MaxTurns);
        turns[^1].Used.ShouldBe((Domain.Entities.SessionContext.MaxTurns + 5) * 100 + 160);
    }

    [Fact]
    public async Task a_compaction_leaves_the_size_unknown_until_the_next_call_and_marks_the_next_turn()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(150_000)));
        await EndTurnAsync();

        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Started, Trigger = ContextCompactionTriggers.Auto });
        (await _sut.GetAsync(Session))!.Compacting.ShouldBeTrue();

        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Ended });
        var compacted = (await _sut.GetAsync(Session))!;
        compacted.Compacting.ShouldBeFalse();
        compacted.CompactedAt.ShouldBe(_time.GetUtcNow());
        compacted.Used.ShouldBeNull();
        compacted.LastCall.ShouldBeNull();
        compacted.Limit.ShouldBe(200_000);

        _time.Advance(TimeSpan.FromSeconds(1));
        await _sut.RecordUsageAsync(Session, Usage(Call(20_000)));
        await EndTurnAsync();

        var turns = (await _sut.GetAsync(Session))!.Turns;
        turns.Select(turn => turn.AfterCompaction).ShouldBe([false, true]);
        turns[^1].Used.ShouldBe(20_160);
    }

    [Fact]
    public async Task a_failed_compaction_keeps_the_size_and_says_why()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(150_000)));
        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Started });
        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Failed, Error = "Not enough messages to compact." });

        var context = (await _sut.GetAsync(Session))!;
        context.Compacting.ShouldBeFalse();
        context.CompactionError.ShouldBe("Not enough messages to compact.");
        context.Used.ShouldBe(150_160);

        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Started });
        (await _sut.GetAsync(Session))!.CompactionError.ShouldBeNull();
    }

    [Fact]
    public async Task a_turns_end_finishes_a_compaction_the_harness_never_said_ended()
    {
        await _sut.RecordUsageAsync(Session, Usage(Call(150_000)));
        await EndTurnAsync();
        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Started });

        await EndTurnAsync();

        var context = (await _sut.GetAsync(Session))!;
        context.Compacting.ShouldBeFalse();
        context.Turns.Count.ShouldBe(1);
        JsonSerializer.Serialize(_broadcaster.Broadcasts[^1].Payload).ShouldContain("\"compacting\":false");
    }

    [Fact]
    public async Task a_call_ends_a_compaction_still_marked_as_running()
    {
        await _sut.RecordCompactionAsync(Session, new ContextCompactionReport { Phase = ContextCompactionPhases.Started });
        await _sut.RecordUsageAsync(Session, Usage(Call(1_000)));

        (await _sut.GetAsync(Session))!.Compacting.ShouldBeFalse();
    }
}
