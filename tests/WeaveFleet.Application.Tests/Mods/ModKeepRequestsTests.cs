using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Mods;

namespace WeaveFleet.Application.Tests.Mods;

public sealed class ModKeepRequestsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ModKeepRequests _requests;

    public ModKeepRequestsTests() => _requests = new ModKeepRequests(_time);

    [Fact]
    public void A_request_is_shown_with_its_note_and_time()
    {
        _requests.Request("u", "s", "chips", "Shows totals");

        var view = _requests.Get("u", "s", "chips").ShouldNotBeNull();
        (view.Note, view.At).ShouldBe(("Shows totals", Start));
    }

    [Fact]
    public void Requests_are_per_user_session_and_name()
    {
        _requests.Request("u", "s", "chips", null);

        _requests.Get("other", "s", "chips").ShouldBeNull();
        _requests.Get("u", "other", "chips").ShouldBeNull();
        _requests.Get("u", "s", "other").ShouldBeNull();
    }

    [Fact]
    public async Task Resolving_ends_the_request_with_the_decision()
    {
        var request = _requests.Request("u", "s", "chips", null);

        _requests.Resolve("u", "s", "chips", new ModKeepDecision(ModKeepOutcome.Kept, 3));

        (await request.Decision).ShouldBe(new ModKeepDecision(ModKeepOutcome.Kept, 3));
        _requests.Get("u", "s", "chips").ShouldBeNull();
    }

    [Fact]
    public void Resolving_without_a_request_does_nothing()
    {
        Should.NotThrow(() => _requests.Resolve("u", "s", "chips", new ModKeepDecision(ModKeepOutcome.Declined)));
    }

    [Fact]
    public async Task A_new_request_replaces_the_old_one_and_ends_it_as_superseded()
    {
        var first = _requests.Request("u", "s", "chips", "first");

        var second = _requests.Request("u", "s", "chips", "second");

        (await first.Decision).Outcome.ShouldBe(ModKeepOutcome.Superseded);
        second.Decision.IsCompleted.ShouldBeFalse();
        _requests.Get("u", "s", "chips")!.Note.ShouldBe("second");
    }

    [Fact]
    public async Task Resolving_a_replaced_request_ends_only_the_new_one()
    {
        var first = _requests.Request("u", "s", "chips", "first");
        var second = _requests.Request("u", "s", "chips", "second");

        _requests.Resolve("u", "s", "chips", new ModKeepDecision(ModKeepOutcome.Declined));

        (await second.Decision).Outcome.ShouldBe(ModKeepOutcome.Declined);
        (await first.Decision).Outcome.ShouldBe(ModKeepOutcome.Superseded);
    }

    [Fact]
    public async Task Clearing_drops_the_request_and_stops_a_waiter()
    {
        var request = _requests.Request("u", "s", "chips", null);

        _requests.Clear("u", "s", "chips");

        _requests.Get("u", "s", "chips").ShouldBeNull();
        (await request.Decision).Outcome.ShouldBe(ModKeepOutcome.Superseded);
    }

    [Fact]
    public async Task Waiting_returns_the_decision_when_it_comes_in_time()
    {
        var request = _requests.Request("u", "s", "chips", null);

        var waiting = _requests.WaitAsync(request, TimeSpan.FromSeconds(60));
        _requests.Resolve("u", "s", "chips", new ModKeepDecision(ModKeepOutcome.Kept, 1));

        (await waiting).ShouldBe(new ModKeepDecision(ModKeepOutcome.Kept, 1));
    }

    [Fact]
    public async Task Waiting_gives_up_on_the_providers_clock_and_leaves_the_request()
    {
        var request = _requests.Request("u", "s", "chips", null);

        var waiting = _requests.WaitAsync(request, TimeSpan.FromSeconds(60));
        _time.Advance(TimeSpan.FromSeconds(59));
        waiting.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(2));

        (await waiting).ShouldBeNull();
        _requests.Get("u", "s", "chips").ShouldNotBeNull();
    }

    [Fact]
    public async Task Waiting_honours_cancellation()
    {
        var request = _requests.Request("u", "s", "chips", null);
        using var cancel = new CancellationTokenSource();

        var waiting = _requests.WaitAsync(request, TimeSpan.FromSeconds(60), cancel.Token);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting);
    }
}
