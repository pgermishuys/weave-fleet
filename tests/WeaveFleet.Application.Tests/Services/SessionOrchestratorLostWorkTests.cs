using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Work an agent left running that was lost (Fleet or its harness process restarted) won't report back, so the session's
/// next prompt tells the agent which, once, as a note to the model.
/// </summary>
public sealed class SessionOrchestratorLostWorkTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _session = new("inst-1");
    private readonly string _repository = Directory.CreateTempSubdirectory("fleet-lost-work-").FullName;

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_repository, recursive: true);
    }

    [Fact]
    public async Task The_next_prompt_names_the_lost_work_and_later_prompts_dont()
    {
        Seed(takesModelNotes: true);
        SeedWork("w1", WorkKinds.Shell, "bun run test:e2e", WorkEndedReasons.Lost);
        SeedWork("w2", WorkKinds.Subagent, "Explore the API", WorkEndedReasons.Lost);
        SeedWork("w3", WorkKinds.Monitor, "tail -f app.log", WorkEndedReasons.Completed);
        var orchestrator = _builder.Build();

        await orchestrator.PromptSessionAsync("s1", "first", null);
        await orchestrator.PromptSessionAsync("s1", "second", null);

        var note = Calls("first").Options!.ModelNotes.ShouldNotBeNull().ShouldHaveSingleItem();
        note.ShouldStartWith("Note from Fleet:");
        note.ShouldContain("- shell: bun run test:e2e");
        note.ShouldContain("- subagent: Explore the API");
        note.ShouldNotContain("tail -f", Case.Sensitive, "work that finished reports back by itself");
        Calls("second").Options!.ModelNotes.ShouldBeNull("the agent is told once");
    }

    [Fact]
    public async Task A_session_with_no_lost_work_gets_no_note()
    {
        Seed(takesModelNotes: true);
        SeedWork("w1", WorkKinds.Shell, "bun run dev", WorkEndedReasons.Cancelled);

        await _builder.Build().PromptSessionAsync("s1", "first", null);

        Calls("first").Options!.ModelNotes.ShouldBeNull();
    }

    [Fact]
    public async Task A_harness_that_drops_Fleets_notes_isnt_told_and_the_work_stays_to_be_told()
    {
        Seed(takesModelNotes: false);
        SeedWork("w1", WorkKinds.Shell, "bun run test:e2e", WorkEndedReasons.Lost);

        await _builder.Build().PromptSessionAsync("s1", "first", null);

        Calls("first").Options!.ModelNotes.ShouldBeNull();
        (await _builder.DelegationRepository.GetUnreportedLostAsync("s1")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_prompt_that_fails_to_send_leaves_the_work_to_be_told_next_time()
    {
        Seed(takesModelNotes: true);
        SeedWork("w1", WorkKinds.Shell, "bun run test:e2e", WorkEndedReasons.Lost);
        _session.SendPromptBehavior = (_, _, _) => throw new InvalidOperationException("Claude Code exited before it read the prompt.");

        (await _builder.Build().PromptSessionAsync("s1", "first", null)).IsFailure.ShouldBeTrue();

        (await _builder.DelegationRepository.GetUnreportedLostAsync("s1")).ShouldHaveSingleItem();
    }

    [Fact]
    public void The_note_stays_short_however_much_was_lost()
    {
        var lost = Enumerable.Range(0, 25)
            .Select(i => new Delegation { Id = $"w{i}", Kind = WorkKinds.Shell, Title = "Bash", Label = new string('x', 400) })
            .ToList();

        var note = LostWorkNote.For(lost)!;

        var lines = note.Split('\n');
        lines.Length.ShouldBe(1 + LostWorkNote.MaxEntries + 1);
        lines[^1].ShouldBe("- and 15 more");
        lines[1].Length.ShouldBe("- shell: ".Length + LostWorkNote.MaxLabelLength);
        LostWorkNote.For([]).ShouldBeNull();
    }

    private (string Text, PromptOptions? Options) Calls(string text) => _session.SendPromptCalls.Single(c => c.Text == text);

    private void Seed(bool takesModelNotes)
    {
        _builder.RegisterHarness("claude-code", "Claude Code", new HarnessCapabilities { TakesModelNotes = takesModelNotes });
        _builder.WorkspaceRepository.Seed(new Workspace { Id = "ws-1", Directory = _repository, IsolationStrategy = "existing" });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            Title = "Run the suite",
            Status = "active",
            Directory = _repository,
            CreatedAt = "2026-10-01",
            HarnessType = "claude-code",
            UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _session);
    }

    private void SeedWork(string workId, string kind, string label, string endedReason)
        => _builder.DelegationRepository.Seed(new Delegation
        {
            Id = $"work-{workId}",
            ParentSessionId = "s1",
            Title = kind,
            Label = label,
            Status = endedReason == WorkEndedReasons.Completed ? "completed" : "cancelled",
            CreatedAt = DateTime.UtcNow.ToString("O"),
            UpdatedAt = DateTime.UtcNow.ToString("O"),
            CompletedAt = DateTime.UtcNow.ToString("O"),
            Kind = kind,
            WorkId = workId,
            EndedReason = endedReason,
        });
}
