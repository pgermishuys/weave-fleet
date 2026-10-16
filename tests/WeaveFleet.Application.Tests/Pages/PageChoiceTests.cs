using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Pages;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Tests.Canvases;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Pages;

/// <summary>
/// fleet_page_show with waitForChoice: the call waits, over as many of the plugin's requests as it takes, until the user
/// sends or skips a pick, writes instead or stops the turn. The session waits on the user meanwhile, and the page says
/// how the wait ended before the agent hears it.
/// </summary>
public sealed class PageChoiceTests : IDisposable
{
    private const string Token = "token-1";
    private const string OpenCodeSessionId = "oc-1";
    private const string SessionId = "ses-1";
    private const string CallId = "call-1";

    private readonly DirectoryInfo _mockups = Directory.CreateTempSubdirectory("fleet-page-choice-");
    private readonly InMemoryCanvasRepository _canvasRepository = new();
    private readonly InMemoryUserPreferenceRepository _preferences = new();
    private readonly FakeTimeProvider _time = new();
    private readonly SessionActivityTracker _activity = new();
    private readonly FleetSessionEvents _events = new();
    private readonly Channel<HarnessEvent> _raised = Channel.CreateUnbounded<HarnessEvent>();
    private readonly PageChoices _choices;
    private readonly PageBridge _bridge;

    public PageChoiceTests()
    {
        _canvasRepository.AddSession(SessionId);
        var callers = new FakeCallers();
        callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, "owner-user"));
        var user = new ScopedUser();
        var sessions = new InMemorySessionRepository();
        sessions.Seed(new Session { Id = SessionId, Directory = _mockups.FullName });
        _events.Attach(SessionId, _raised.Writer);
        _choices = new PageChoices(_events, _activity, _time);
        _bridge = new PageBridge(
            [callers], user, new CanvasService(_canvasRepository, new FakeEventBroadcaster(), user), new FakePageStore(), sessions,
            choices: _choices, choiceFeature: new PageChoiceFeature(_preferences));
        _preferences.SetAsync(PageChoiceFeature.PreferenceKey, "true").GetAwaiter().GetResult();
    }

    public void Dispose() => _mockups.Delete(recursive: true);

    [Fact]
    public async Task A_page_that_waits_marks_its_canvas_and_the_session_waits_on_the_user()
    {
        var shown = await ShowAsync();

        shown.Value!.Choice.ShouldBe(PageChoiceOutput.StillWaiting);
        var choice = (await PageAsync()).Choice!;
        (choice.CallId, choice.Status).ShouldBe((CallId, PageChoiceState.Waiting));
        Statuses().ShouldBe([ActivityStatuses.WaitingInput]);
    }

    [Fact]
    public async Task The_wait_outlives_a_round_and_returns_the_pick_the_user_sent()
    {
        var shown = await ShowAsync();

        var round = _bridge.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId);
        _time.Advance(PageChoices.Round);
        (await round).Value!.Choice.ShouldBe(PageChoiceOutput.StillWaiting);

        var next = _bridge.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId);
        _activity.Update(SessionId, ActivityStatuses.WaitingInput, "owner-user");
        _choices.End(SessionId, CallId, PageChoiceAnswer.Picked("b", "Layout B")).ShouldBeTrue();
        var answered = (await next).Value!;

        answered.Title.ShouldBe("Picked Layout B");
        answered.Output.ShouldStartWith("{\"answered\":true,\"value\":\"b\",\"summary\":\"Layout B\"}");
        var choice = (await PageAsync()).Choice!;
        (choice.Status, choice.Value, choice.Summary).ShouldBe((PageChoiceState.Answered, "b", "Layout B"));
        Statuses().ShouldBe([ActivityStatuses.WaitingInput, ActivityStatuses.Busy]);
        _choices.End(SessionId, CallId, PageChoiceAnswer.Picked("a", "Layout A")).ShouldBeFalse();
    }

    [Fact]
    public async Task Writing_instead_ends_the_wait_without_a_pick()
    {
        var shown = await ShowAsync();
        var round = _bridge.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId);

        _choices.EndAll(SessionId, PageChoiceAnswer.NotAnswered(PageChoiceReasons.WroteInstead)).ShouldBe(1);

        (await round).Value!.Output.ShouldStartWith("{\"answered\":false,\"reason\":\"wrote instead\"}");
        (await PageAsync()).Choice!.Reason.ShouldBe(PageChoiceReasons.WroteInstead);
    }

    [Fact]
    public async Task A_round_that_goes_away_stops_the_wait_and_leaves_the_status_to_the_harness()
    {
        var shown = await ShowAsync();
        using var turn = new CancellationTokenSource();
        var round = _bridge.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId, turn.Token);

        await turn.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => round);
        (await PageAsync()).Choice!.Reason.ShouldBe(PageChoiceReasons.Stopped);
        Statuses().ShouldBe([ActivityStatuses.WaitingInput]);
    }

    [Fact]
    public async Task A_wait_Fleet_does_not_know_was_cut_by_a_restart()
    {
        var shown = await ShowAsync();
        var restarted = new PageBridge(
            [NewCallers()], new ScopedUser(), new CanvasService(_canvasRepository, new FakeEventBroadcaster(), new ScopedUser()),
            new FakePageStore(), new InMemorySessionRepository(), choices: new PageChoices(_events, _activity, _time));

        var round = await restarted.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId);

        round.Value!.Choice!.Answer!.Reason.ShouldBe(PageChoiceReasons.Restarted);
        (await PageAsync()).Choice!.Status.ShouldBe(PageChoiceState.NotAnswered);
    }

    [Fact]
    public async Task Nobody_picking_ends_the_wait_after_a_day()
    {
        var shown = await ShowAsync();
        _time.Advance(PageChoices.Cap);

        var round = await _bridge.WaitForChoiceAsync(Token, OpenCodeSessionId, CallId, shown.Value!.CanvasId);

        round.Value!.Choice!.Answer!.Reason.ShouldBe(PageChoiceReasons.TimedOut);
    }

    [Fact]
    public async Task With_the_switch_off_the_page_is_shown_and_the_call_says_it_did_not_wait()
    {
        await _preferences.SetAsync(PageChoiceFeature.PreferenceKey, "false");

        var shown = await ShowAsync();

        shown.Value!.Choice.ShouldBeNull();
        shown.Value!.Output.ShouldEndWith(PageBridge.CantWait);
        (await PageAsync()).Choice.ShouldBeNull();
        Statuses().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_in_the_conversation_cannot_wait()
    {
        var shown = await _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup(), "Layouts", PageBridge.InConversation, waitForChoice: true, callId: CallId);

        shown.Error!.Message.ShouldBe(PageBridge.ChoiceNeedsTab);
    }

    private Task<CanvasResult<CanvasToolOutput>> ShowAsync()
        => _bridge.ShowAsync(Token, OpenCodeSessionId, Mockup(), "Layouts", waitForChoice: true, callId: CallId);

    private async Task<PageState> PageAsync()
        => PageState.Parse((await _canvasRepository.ListBySessionIdAsync(SessionId)).ShouldHaveSingleItem().StateJson);

    private List<string> Statuses()
    {
        var statuses = new List<string>();
        while (_raised.Reader.TryRead(out var evt))
            statuses.Add(evt.Payload!.Value.GetProperty("status").GetProperty("type").GetString()!);
        return statuses;
    }

    private static FakeCallers NewCallers()
    {
        var callers = new FakeCallers();
        callers.Add(Token, OpenCodeSessionId, new HarnessCanvasCaller(SessionId, "owner-user"));
        return callers;
    }

    private string Mockup()
    {
        var path = Path.Combine(_mockups.FullName, "layouts", "index.html");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<!doctype html><title>Layouts</title><button data-fleet-choice=\"b\">Layout B</button>");
        return path;
    }
}
