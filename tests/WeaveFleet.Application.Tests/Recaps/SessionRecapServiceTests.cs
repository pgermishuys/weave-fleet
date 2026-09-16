using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Recaps;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Domain.Repositories;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Recaps;

public sealed class SessionRecapServiceTests : IAsyncDisposable
{
    private const string SessionId = "s1";
    private const string UserId = "u1";
    private const string Answer = "You're adding live progress to rows. Next, decide whether finished todos stay.";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeEventBroadcaster _broadcaster = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly InstanceTracker _instances = new();
    private readonly FakeHarnessRegistry _harnesses = new();
    private readonly FakeHarnessSession _harness = new("inst-1") { OffTheRecordAnswer = Answer };
    private readonly FakeRecapPreference _preference = new() { Enabled = true };
    private readonly SessionFocusTracker _focus = new();
    private readonly FakeHarness _openCode = new("opencode", "OpenCode", new HarnessCapabilities { SupportsOffTheRecordPrompt = true });
    private readonly SessionRecapService _sut;

    public SessionRecapServiceTests()
    {
        var sessions = new InMemorySessionRepository();
        sessions.InsertAsync(new Session { Id = SessionId, InstanceId = "inst-1", HarnessType = "opencode", UserId = UserId }).GetAwaiter().GetResult();
        _instances.Register("inst-1", _harness);
        _harnesses.Register(_openCode);
        _sut = new SessionRecapService(
            _activity,
            _instances,
            _harnesses,
            _broadcaster,
            _preference,
            _focus,
            TestServiceScopeFactory.Create(services => services.AddSingleton<ISessionRepository>(sessions)),
            _clock,
            NullLogger<SessionRecapService>.Instance);
    }

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task writes_a_recap_three_minutes_after_a_turn_ends_while_no_one_is_looking()
    {
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
        Recaps().ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(1));
        var recap = await SingleRecapAsync();

        recap.Topic.ShouldBe($"session:{SessionId}");
        recap.UserId.ShouldBe(UserId);
        recap.Payload.GetProperty("text").GetString().ShouldBe(Answer);
        _sut.Get(SessionId)!.Text.ShouldBe(Answer);
        _harness.OffTheRecordPrompts.ShouldBe([SessionRecapService.Prompt]);
    }

    [Fact]
    public async Task writes_nothing_while_a_tab_is_looking()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(10));

        Recaps().ShouldBeEmpty();
        _harness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task leaving_soon_after_the_turn_still_writes_it_three_minutes_after_the_turn()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.SetFocus("tab-1", SessionId, focused: false);
        _clock.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        Recaps().ShouldBeEmpty();

        _clock.Advance(TimeSpan.FromSeconds(1));
        await SingleRecapAsync();
    }

    [Fact]
    public async Task leaving_late_writes_it_shortly_after_you_leave()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromSeconds(210));
        _sut.SetFocus("tab-1", SessionId, focused: false);
        _clock.Advance(SessionRecapService.MinimumAway);

        await SingleRecapAsync();
    }

    [Fact]
    public async Task leaving_once_the_cache_is_too_old_writes_nothing()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _clock.Advance(SessionRecapService.CacheWindow);
        _sut.SetFocus("tab-1", SessionId, focused: false);
        _clock.Advance(TimeSpan.FromMinutes(10));

        Recaps().ShouldBeEmpty();
    }

    [Fact]
    public async Task closing_the_tab_counts_as_leaving()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _sut.RemoveConnection("tab-1");
        _clock.Advance(TimeSpan.FromMinutes(3));

        await SingleRecapAsync();
    }

    [Fact]
    public async Task switching_the_tab_to_another_session_counts_as_leaving()
    {
        _sut.SetFocus("tab-1", SessionId, focused: true);
        await TurnsAsync(3);

        _sut.SetFocus("tab-1", "another-session", focused: true);
        _clock.Advance(TimeSpan.FromMinutes(3));

        await SingleRecapAsync();
    }

    [Fact]
    public async Task coming_back_before_it_is_written_cancels_it()
    {
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _sut.SetFocus("tab-1", SessionId, focused: true);
        _clock.Advance(TimeSpan.FromMinutes(10));

        Recaps().ShouldBeEmpty();
    }

    [Fact]
    public async Task a_new_turn_cancels_it()
    {
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _sut.OnActivityChanged(SessionId, "busy");
        _clock.Advance(TimeSpan.FromMinutes(10));

        Recaps().ShouldBeEmpty();
    }

    [Fact]
    public async Task waits_for_three_prompts_before_the_first_recap()
    {
        await TurnsAsync(2);

        _clock.Advance(TimeSpan.FromMinutes(10));

        Recaps().ShouldBeEmpty();
    }

    [Fact]
    public async Task waits_for_two_prompts_between_recaps()
    {
        await TurnsAsync(3);
        _clock.Advance(TimeSpan.FromMinutes(3));
        await SingleRecapAsync();

        await TurnsAsync(1);
        _clock.Advance(TimeSpan.FromMinutes(10));
        Recaps().Count(HasText).ShouldBe(1);

        await TurnsAsync(1);
        _clock.Advance(TimeSpan.FromMinutes(3));
        await WaitForAsync(() => Recaps().Count(HasText) == 2);
    }

    [Fact]
    public async Task your_next_prompt_clears_it()
    {
        await TurnsAsync(3);
        _clock.Advance(TimeSpan.FromMinutes(3));
        await SingleRecapAsync();

        await _sut.OnPromptSentAsync(SessionId, UserId, CancellationToken.None);

        _sut.Get(SessionId).ShouldBeNull();
        var cleared = Recaps().Last();
        HasText(cleared).ShouldBeFalse();
    }

    [Fact]
    public async Task does_nothing_when_the_setting_is_off()
    {
        _preference.Enabled = false;
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50);

        Recaps().ShouldBeEmpty();
        _harness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task skips_harnesses_that_cannot_answer_off_the_record()
    {
        _openCode.Capabilities = new HarnessCapabilities { SupportsOffTheRecordPrompt = false };
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50);

        _harness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task skips_a_session_whose_subagents_are_still_working()
    {
        await TurnsAsync(3);
        _activity.RegisterChild("child-1", SessionId);
        _activity.Update("child-1", "busy", UserId);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50);

        _harness.OffTheRecordPrompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task caps_a_long_answer()
    {
        _harness.OffTheRecordAnswer = new string('a', 1000);
        await TurnsAsync(3);

        _clock.Advance(TimeSpan.FromMinutes(3));
        var recap = await SingleRecapAsync();

        recap.Payload.GetProperty("text").GetString()!.Length.ShouldBe(400);
    }

    /// <summary>Prompt, then the harness goes busy and back to idle: one finished turn.</summary>
    private async Task TurnsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _sut.OnPromptSentAsync(SessionId, UserId, CancellationToken.None);
            _activity.Update(SessionId, "busy", UserId);
            _sut.OnActivityChanged(SessionId, "busy");
            _activity.Update(SessionId, "idle", UserId);
            _sut.OnActivityChanged(SessionId, "idle");
        }
    }

    private List<FakeEventBroadcaster.BroadcastRecord> Recaps()
        => [.. _broadcaster.Broadcasts.Where(b => b.Type == "session.recap")];

    private static bool HasText(FakeEventBroadcaster.BroadcastRecord record)
        => record.Payload.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String;

    private async Task<FakeEventBroadcaster.BroadcastRecord> SingleRecapAsync()
    {
        await WaitForAsync(() => Recaps().Count > 0);
        return Recaps().ShouldHaveSingleItem();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(10);

        condition().ShouldBeTrue();
    }

    private sealed class FakeRecapPreference : IRecapPreference
    {
        public bool Enabled { get; set; }

        public Task<bool> IsEnabledAsync(string userId, CancellationToken ct) => Task.FromResult(Enabled);
    }
}
