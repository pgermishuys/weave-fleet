using Microsoft.Extensions.Logging.Abstractions;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>Which cut-off sessions Fleet continues by itself, and the pace it does it at.</summary>
public sealed class RestartContinuerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static SessionInterruption Cut(TimeSpan ago, bool waiting = false) => new()
    {
        SessionId = "s1",
        UserId = "local-user",
        At = Now - ago,
        Waiting = waiting,
    };

    private static Session Open(string retention = "active") => new() { Id = "s1", RetentionStatus = retention };

    [Fact]
    public void A_session_cut_off_mid_turn_a_minute_ago_qualifies()
    {
        RestartContinuer.Qualifies(Cut(TimeSpan.FromMinutes(1)), Open(), Now, folderExists: true, harnessReady: true).ShouldBeTrue();
    }

    [Fact]
    public void A_lost_question_never_qualifies()
    {
        RestartContinuer.Qualifies(Cut(TimeSpan.FromMinutes(1), waiting: true), Open(), Now, true, true).ShouldBeFalse();
    }

    [Theory]
    [InlineData(119, true)]
    [InlineData(120, false)]
    [InlineData(600, false)]
    public void Only_a_session_cut_off_less_than_two_hours_ago_qualifies(int minutes, bool expected)
    {
        RestartContinuer.Qualifies(Cut(TimeSpan.FromMinutes(minutes)), Open(), Now, true, true).ShouldBe(expected);
    }

    [Fact]
    public void An_archived_session_a_missing_folder_or_a_harness_that_is_not_ready_do_not_qualify()
    {
        var cut = Cut(TimeSpan.FromMinutes(1));
        RestartContinuer.Qualifies(cut, Open("archived"), Now, true, true).ShouldBeFalse();
        RestartContinuer.Qualifies(cut, Open(), Now, folderExists: false, harnessReady: true).ShouldBeFalse();
        RestartContinuer.Qualifies(cut, Open(), Now, folderExists: true, harnessReady: false).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_go_at_a_time_and_the_next_one_when_a_turn_starts()
    {
        var continuer = new RestartContinuer(NullLogger<RestartContinuer>.Instance);
        var sent = new List<string>();
        var run = continuer.RunAsync("u", ["a", "b", "c", "d"], (id, _) =>
        {
            lock (sent)
                sent.Add(id);
            return Task.FromResult(true);
        }, CancellationToken.None);

        await WaitForAsync(() => sent.Count == 2);
        await Task.Delay(100);
        sent.ShouldBe(["a", "b"]);

        continuer.NotifyTurnStarted("a");
        await WaitForAsync(() => sent.Count == 3);
        sent.ShouldBe(["a", "b", "c"]);

        continuer.NotifyTurnStarted("b");
        continuer.NotifyTurnStarted("c");
        await WaitForAsync(() => sent.Count == 4);
        continuer.NotifyTurnStarted("d");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        sent.ShouldBe(["a", "b", "c", "d"]);
    }

    [Fact]
    public async Task Stop_all_cancels_the_sends_not_yet_made()
    {
        var continuer = new RestartContinuer(NullLogger<RestartContinuer>.Instance);
        var sent = new List<string>();
        var run = continuer.RunAsync("u", ["a", "b", "c"], (id, _) =>
        {
            lock (sent)
                sent.Add(id);
            return Task.FromResult(true);
        }, CancellationToken.None);

        await WaitForAsync(() => sent.Count == 2);
        continuer.IsPending("c").ShouldBeTrue();

        continuer.Stop("someone-else").ShouldBeEmpty();
        continuer.Stop("u").ShouldBe(["c"]);
        continuer.IsPending("c").ShouldBeFalse();

        continuer.NotifyTurnStarted("a");
        continuer.NotifyTurnStarted("b");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        sent.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_turn_that_never_starts_does_not_hold_the_rest()
    {
        var continuer = new RestartContinuer(NullLogger<RestartContinuer>.Instance) { StartTimeout = TimeSpan.FromMilliseconds(50) };
        var sent = new List<string>();
        await continuer.RunAsync("u", ["a", "b", "c"], (id, _) =>
        {
            lock (sent)
                sent.Add(id);
            return Task.FromResult(true);
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        sent.ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public async Task A_failed_send_frees_its_slot()
    {
        var continuer = new RestartContinuer(NullLogger<RestartContinuer>.Instance);
        var sent = new List<string>();
        var run = continuer.RunAsync("u", ["a", "b", "c"], (id, _) =>
        {
            lock (sent)
                sent.Add(id);
            return Task.FromResult(id == "a" ? false : true);
        }, CancellationToken.None);

        await WaitForAsync(() => sent.Count == 3);
        continuer.NotifyTurnStarted("b");
        continuer.NotifyTurnStarted("c");
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_message_is_labelled_as_sent_by_Fleet_after_the_restart_and_queues()
    {
        var options = SessionInterruptions.ContinueOptions(automatic: true);
        options.SentByMod.ShouldBe("Fleet after the restart");
        options.Delivery.ShouldBe(WeaveFleet.Domain.Harnesses.PromptDelivery.Queue);
        SessionInterruptions.ContinueOptions(automatic: false).SentByMod.ShouldBeNull();
    }

    [Fact]
    public void The_view_says_when_Fleet_will_continue_the_session_by_itself()
    {
        SessionInterruptionView.From(Cut(TimeSpan.FromMinutes(1))).Auto.ShouldBeFalse();
        SessionInterruptionView.From(Cut(TimeSpan.FromMinutes(1)), auto: true).Auto.ShouldBeTrue();
    }

    [Fact]
    public void The_interrupted_event_carries_auto_on_the_wire()
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            new SessionInterruptionChanged("s1", SessionInterruptionView.From(Cut(TimeSpan.FromMinutes(1)), auto: true)),
            ApplicationJsonContext.Default.SessionInterruptionChanged);
        payload.GetProperty("interruption").GetProperty("auto").GetBoolean().ShouldBeTrue();
        payload.GetProperty("sessionId").GetString().ShouldBe("s1");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held.");
            await Task.Delay(10);
        }
    }
}
