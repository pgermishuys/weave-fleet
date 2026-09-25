using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A side question (<c>/btw</c>) goes to a fork of the session, a hidden Fleet session of its own, and never to the
/// session itself. Its prompts carry the boundary note; closing it deletes the fork and nothing else.
/// </summary>
public sealed class SessionOrchestratorSideConversationTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _session = new("inst-1");
    private readonly FakeHarnessSession _side = new("inst-side");
    private FakeHarnessRuntime _runtime = null!;

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _side.DisposeAsync();
    }

    private void Seed(bool supportsSideConversations = true, string retentionStatus = "active")
    {
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsSideConversations = supportsSideConversations });
        _runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(_side);
        _builder.WorkspaceRepository.Seed(new Workspace { Id = "ws-1", Directory = "/tmp/repo", IsolationStrategy = "worktree", SourceDirectory = "/tmp/src" });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            Title = "Refactor the parser",
            Status = "active",
            Directory = "/tmp/repo",
            CreatedAt = "2026-01-01",
            RetentionStatus = retentionStatus,
            HarnessType = "opencode",
            UserId = "user-1",
            SelectedAgent = "build",
            SelectedProviderId = "anthropic",
            SelectedModelId = "claude-sonnet",
        });
        _builder.InstanceTracker.Register("inst-1", _session);
        _session.SideConversationFork = new SideConversationFork("ses_fork", "msg_boundary");
    }

    [Fact]
    public async Task asks_in_a_fork_of_the_session_and_leaves_the_session_alone()
    {
        Seed();
        var orchestrator = _builder.Build();

        var result = await orchestrator.AskSideQuestionAsync("s1", "what did I ask first?", options: null, correlationId: null);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        _session.SendPromptCalls.ShouldBeEmpty();

        var resume = _runtime.ResumeCalls.ShouldHaveSingleItem();
        resume.ResumeToken.ShouldBe("ses_fork");
        resume.ParentSessionId.ShouldBe("s1");

        var side = result.Value.SideConversation;
        side.SideOfSessionId.ShouldBe("s1");
        side.SideBoundaryMessageId.ShouldBe("msg_boundary");
        side.IsHidden.ShouldBeTrue();
        side.WorkspaceId.ShouldBe("ws-1");
        side.Title.ShouldBe("btw: what did I ask first?");
        // The session's own choices, so the copied conversation is asked as the session's prompts are.
        side.SelectedAgent.ShouldBe("build");
        side.SelectedModelId.ShouldBe("claude-sonnet");

        var (text, options) = _side.SendPromptCalls.ShouldHaveSingleItem();
        text.ShouldBe("what did I ask first?");
        options.ShouldNotBeNull().ModelNotes.ShouldBe([SideConversations.BoundaryInstruction]);
        options.Agent.ShouldBe("build");
        options.ModelId.ShouldBe("claude-sonnet");
        result.Value.Prompt.MessageId.ShouldNotBeNull().ShouldStartWith("msg_");

        // Nothing lists it, and nothing announced it.
        (await _builder.SessionRepository.ListAsync()).Select(s => s.Id).ShouldBe(["s1"]);
        _builder.EventBroadcaster.Broadcasts.ShouldNotContain(b => b.Type == "session_created");
        (await orchestrator.GetSideConversationAsync("s1")).Value.ShouldNotBeNull().Id.ShouldBe(side.Id);
    }

    [Fact]
    public async Task a_second_question_goes_to_the_open_side_conversation()
    {
        Seed();
        var orchestrator = _builder.Build();

        var first = await orchestrator.AskSideQuestionAsync("s1", "one", null, null);
        var second = await orchestrator.AskSideQuestionAsync("s1", "two", null, null);

        second.Value.SideConversation.Id.ShouldBe(first.Value.SideConversation.Id);
        _session.SideConversationForks.ShouldBe(1);
        _side.SendPromptCalls.Select(c => c.Text).ShouldBe(["one", "two"], ignoreOrder: true);
        _side.SendPromptCalls.ShouldAllBe(c => c.Options!.ModelNotes!.Single() == SideConversations.BoundaryInstruction);
    }

    [Fact]
    public async Task says_why_when_the_harness_cant_fork()
    {
        Seed(supportsSideConversations: false);

        var result = await _builder.Build().AskSideQuestionAsync("s1", "why?", null, null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("OpenCode sessions can't fork, so /btw isn't available here.");
        _session.SideConversationForks.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task asks_for_a_question(string? question)
    {
        Seed();

        var result = await _builder.Build().AskSideQuestionAsync("s1", question, null, null);

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldBe("Type a question after /btw.");
    }

    [Fact]
    public async Task refuses_an_archived_session()
    {
        Seed(retentionStatus: "archived");

        var result = await _builder.Build().AskSideQuestionAsync("s1", "why?", null, null);

        result.IsFailure.ShouldBeTrue();
        _session.SideConversationForks.ShouldBe(0);
    }

    [Fact]
    public async Task a_side_conversation_has_none_of_its_own()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;

        var result = await orchestrator.AskSideQuestionAsync(side.Id, "nested?", null, null);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task discarding_hides_it_at_once_and_deletes_the_fork_only_once_its_undo_window_has_passed()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;

        var result = await orchestrator.CloseSideConversationAsync("s1");

        result.IsSuccess.ShouldBeTrue();
        (await orchestrator.GetSideConversationAsync("s1")).Value.ShouldBeNull();
        _side.DeleteCalled.ShouldBeFalse();
        (await _builder.SessionRepository.GetByIdAsync(side.Id)).ShouldNotBeNull().SideDiscardedAt.ShouldNotBeNull();

        // The sweeper's call once the window has passed.
        await orchestrator.DeleteDiscardedSideConversationAsync(side.Id);

        _side.DeleteCalled.ShouldBeTrue();
        _session.DeleteCalled.ShouldBeFalse();
        (await _builder.SessionRepository.GetByIdAsync(side.Id)).ShouldBeNull();
        (await _builder.SessionRepository.GetByIdAsync("s1")).ShouldNotBeNull();
        (await _builder.WorkspaceRepository.GetByIdAsync("ws-1")).ShouldNotBeNull().CleanedUpAt.ShouldBeNull();
        (await orchestrator.GetSideConversationAsync("s1")).Value.ShouldBeNull();
    }

    [Fact]
    public async Task undo_brings_it_back_as_it_was_and_the_sweeper_leaves_it_alone()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;
        await orchestrator.SetSideConversationMinimizedAsync("s1", minimized: true);
        await orchestrator.CloseSideConversationAsync("s1");

        var restored = await orchestrator.RestoreSideConversationAsync("s1");

        restored.IsSuccess.ShouldBeTrue(restored.IsFailure ? restored.Error.Description : null);
        restored.Value.Id.ShouldBe(side.Id);
        restored.Value.SideMinimized.ShouldBeTrue();
        (await orchestrator.GetSideConversationAsync("s1")).Value.ShouldNotBeNull().Id.ShouldBe(side.Id);

        await orchestrator.DeleteDiscardedSideConversationAsync(side.Id);
        _side.DeleteCalled.ShouldBeFalse();
    }

    [Fact]
    public async Task a_new_side_question_ends_the_undo_window_of_a_discarded_one()
    {
        Seed();
        var orchestrator = _builder.Build();
        var first = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;
        await orchestrator.CloseSideConversationAsync("s1");
        var secondHarness = new FakeHarnessSession("inst-side-2");
        _runtime.ResumeBehavior = (_, _) => Task.FromResult<IHarnessSession>(secondHarness);
        _session.SideConversationFork = new SideConversationFork("ses_fork2", "msg_boundary");

        var second = await orchestrator.AskSideQuestionAsync("s1", "two", null, null);

        second.IsSuccess.ShouldBeTrue();
        _side.DeleteCalled.ShouldBeTrue();
        (await _builder.SessionRepository.GetByIdAsync(first.Id)).ShouldBeNull();
        (await orchestrator.GetUndoableSideConversationAsync("s1")).Value.ShouldBeNull();
        // Nothing left for Undo to bring back.
        (await orchestrator.RestoreSideConversationAsync("s1")).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task undo_still_guards_against_a_newer_open_side_question()
    {
        // Asking deletes a discarded one first; this is the race where both exist anyway.
        Seed();
        var orchestrator = _builder.Build();
        var first = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;
        await orchestrator.CloseSideConversationAsync("s1");
        _builder.SessionRepository.Seed(new Session
        {
            Id = "side-newer", WorkspaceId = "ws-1", InstanceId = "inst-x", Title = "btw: two", Status = "active",
            Directory = "/tmp/repo", CreatedAt = "2099-01-01", HarnessType = "opencode", UserId = "user-1", SideOfSessionId = "s1",
        });

        var restored = await orchestrator.RestoreSideConversationAsync("s1");

        restored.IsFailure.ShouldBeTrue();
        restored.Error.Code.ShouldBe("General.Conflict");
        (await _builder.SessionRepository.GetByIdAsync(first.Id)).ShouldNotBeNull().SideDiscardedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_discarded_one_says_how_long_undo_is_still_offered()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;
        await orchestrator.CloseSideConversationAsync("s1");

        var undoable = (await orchestrator.GetUndoableSideConversationAsync("s1")).Value.ShouldNotBeNull();

        undoable.SideConversation.Id.ShouldBe(side.Id);
        undoable.UndoLeft.ShouldBeGreaterThan(TimeSpan.FromSeconds(6));
        undoable.UndoLeft.ShouldBeLessThanOrEqualTo(SideConversations.UndoOffered);

        // Once the offer has run out, there's nothing to show.
        var discarded = (await _builder.SessionRepository.GetByIdAsync(side.Id))!;
        discarded.SideDiscardedAt = DateTime.UtcNow.AddSeconds(-9).ToString("O");
        (await orchestrator.GetUndoableSideConversationAsync("s1")).Value.ShouldBeNull();
    }

    [Fact]
    public async Task remembers_the_newest_answer_seen()
    {
        Seed();
        var orchestrator = _builder.Build();
        await orchestrator.AskSideQuestionAsync("s1", "one", null, null);

        var seen = await orchestrator.SetSideConversationSeenAsync("s1", "msg_answer");

        seen.Value.SideSeenAnswerId.ShouldBe("msg_answer");
        (await orchestrator.GetSideConversationAsync("s1")).Value!.SideSeenAnswerId.ShouldBe("msg_answer");
    }

    [Theory]
    [InlineData(-1, 7)]
    [InlineData(-8, 0)]
    [InlineData(-30, 0)]
    public void undo_left_counts_from_the_discard(int secondsAgo, int wholeSecondsLeft)
    {
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var discardedAt = now.AddSeconds(secondsAgo).UtcDateTime.ToString("O");

        ((int)SideConversations.UndoLeft(discardedAt, now).TotalSeconds).ShouldBe(wholeSecondsLeft);
        SideConversations.UndoLeft(null, now).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task minimized_is_kept_with_it_and_asking_again_opens_it()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;

        (await orchestrator.SetSideConversationMinimizedAsync("s1", minimized: true)).Value.SideMinimized.ShouldBeTrue();
        (await orchestrator.GetSideConversationAsync("s1")).Value!.SideMinimized.ShouldBeTrue();

        var again = await orchestrator.AskSideQuestionAsync("s1", "two", null, null);
        again.Value.SideConversation.Id.ShouldBe(side.Id);
        again.Value.SideConversation.SideMinimized.ShouldBeFalse();
        (await orchestrator.GetSideConversationAsync("s1")).Value!.SideMinimized.ShouldBeFalse();
    }

    [Fact]
    public async Task deleting_the_side_conversation_as_a_session_leaves_the_folder_too()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;

        await orchestrator.DeleteSessionAsync(side.Id);

        _side.DeleteCalled.ShouldBeTrue();
        (await _builder.WorkspaceRepository.GetByIdAsync("ws-1")).ShouldNotBeNull().CleanedUpAt.ShouldBeNull();
    }

    [Fact]
    public async Task deleting_the_session_deletes_its_side_conversation_even_one_waiting_out_its_undo()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;
        await orchestrator.CloseSideConversationAsync("s1");

        await orchestrator.DeleteSessionAsync("s1");

        _side.DeleteCalled.ShouldBeTrue();
        (await _builder.SessionRepository.GetByIdAsync(side.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task keeping_it_lists_it_in_a_workspace_of_its_own_and_lifts_the_boundary()
    {
        Seed();
        var orchestrator = _builder.Build();
        var side = (await orchestrator.AskSideQuestionAsync("s1", "one", null, null)).Value.SideConversation;

        var kept = await orchestrator.KeepSideConversationAsync("s1");

        kept.IsSuccess.ShouldBeTrue(kept.IsFailure ? kept.Error.Description : null);
        kept.Value.KeptFromSide.ShouldBeTrue();
        kept.Value.WorkspaceId.ShouldNotBe("ws-1");
        (await _builder.SessionRepository.ListAsync()).Select(s => s.Id).ShouldContain(side.Id);
        _builder.EventBroadcaster.Broadcasts.ShouldContain(b => b.Type == "session_created");
        (await orchestrator.GetSideConversationAsync("s1")).Value.ShouldBeNull();

        await orchestrator.PromptSessionAsync(side.Id, "carry on", null);
        _side.SendPromptCalls.Single(c => c.Text == "carry on").Options!.ModelNotes.ShouldBe([SideConversations.KeptNotice]);
    }

    [Fact]
    public async Task an_ordinary_prompt_carries_no_notes()
    {
        Seed();

        await _builder.Build().PromptSessionAsync("s1", "hello", null);

        _session.SendPromptCalls.ShouldHaveSingleItem().Options!.ModelNotes.ShouldBeNull();
    }

    [Theory]
    [InlineData("what did I ask first?", "btw: what did I ask first?")]
    [InlineData("  spread\nover   lines ", "btw: spread over lines")]
    public void titles_the_side_conversation_after_its_first_question(string question, string title)
        => SideConversations.Title(question).ShouldBe(title);

    [Fact]
    public void shortens_a_long_title()
        => SideConversations.Title(new string('a', 200)).Length.ShouldBe(SideConversations.MaxTitleLength);
}
