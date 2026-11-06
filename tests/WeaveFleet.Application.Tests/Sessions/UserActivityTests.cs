using Microsoft.Extensions.Time.Testing;
using Shouldly;
using WeaveFleet.Application.Updates;
using WeaveFleet.Testing.Builders;

namespace WeaveFleet.Application.Tests.Sessions;

/// <summary>A prompt, an answer to a question and an answer to a permission ask each count as the machine being in use, so the automatic update waits.</summary>
public sealed class UserActivityTests
{
    private readonly UserActivityClock _clock = new(new FakeTimeProvider());
    private readonly SessionOrchestratorBuilder _builder;

    public UserActivityTests() => _builder = new SessionOrchestratorBuilder().WithUserActivity(_clock);

    [Fact]
    public async Task A_prompt_touches_the_clock_before_its_turn_starts()
    {
        _builder.Build();
        await _builder.Prompting.PromptSessionAsync("no-such-session", "hello");
        _clock.Last.ShouldNotBeNull();
    }

    [Fact]
    public async Task Answering_a_question_touches_the_clock()
    {
        _builder.Build();
        await _builder.Asks.AnswerQuestionAsync("no-such-session", "q1", [["yes"]]);
        _clock.Last.ShouldNotBeNull();
    }

    [Fact]
    public async Task Rejecting_a_question_touches_the_clock()
    {
        _builder.Build();
        await _builder.Asks.RejectQuestionAsync("no-such-session", "q1");
        _clock.Last.ShouldNotBeNull();
    }

    [Fact]
    public async Task Answering_a_permission_ask_touches_the_clock()
    {
        _builder.Build();
        await _builder.Asks.ReplyToPermissionAsync("no-such-session", "p1", "once", null);
        _clock.Last.ShouldNotBeNull();
    }
}
