using Shouldly;
using WeaveFleet.Application.Memory;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A session's instructions keep the memory notes it started with, so a note changed while it runs reaches it with its
/// next prompt, in the notes the harness gives the model unseen.
/// </summary>
public sealed class SessionOrchestratorMemoryTests : IAsyncDisposable
{
    private readonly string _repository = Directory.CreateTempSubdirectory("fleet-memory-repo-").FullName;
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessSession _session = new("inst-1");
    private readonly AgentMemoryService _memory;

    public SessionOrchestratorMemoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_repository, ".git"));
        _memory = new AgentMemoryService(
            new FakeMemoryStore(), new InMemoryUserPreferenceRepository(), new TestUserContext("user-1"), told: new AgentMemorySessions());
        _builder.WithAgentMemory(_memory);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_repository, recursive: true);
    }

    private void Seed(string harnessType, bool passesModelNotes)
    {
        _builder.RegisterHarness(harnessType, harnessType, new HarnessCapabilities { SupportsSideConversations = passesModelNotes });
        _builder.WorkspaceRepository.Seed(new Workspace { Id = "ws-1", Directory = _repository, IsolationStrategy = "existing" });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "s1",
            WorkspaceId = "ws-1",
            InstanceId = "inst-1",
            Title = "Fix the flaky test",
            Status = "active",
            Directory = _repository,
            CreatedAt = "2026-10-01",
            HarnessType = harnessType,
            UserId = "user-1",
        });
        _builder.InstanceTracker.Register("inst-1", _session);
    }

    [Fact]
    public async Task a_note_changed_while_the_session_runs_goes_with_its_next_prompt_and_only_that_one()
    {
        Seed("opencode", passesModelNotes: true);
        await _memory.SetEnabledAsync(true);
        var orchestrator = _builder.Build();

        await orchestrator.PromptSessionAsync("s1", "first", null);
        var note = (await _memory.AddAsync("machine", null, "Use gh for GitHub.")).Value;
        await orchestrator.PromptSessionAsync("s1", "second", null);
        await orchestrator.PromptSessionAsync("s1", "third", null);

        var calls = _session.SendPromptCalls;
        calls.Single(c => c.Text == "first").Options!.ModelNotes.ShouldBeNull("the session starts with the notes in its instructions");
        calls.Single(c => c.Text == "second").Options!.ModelNotes.ShouldNotBeNull().ShouldHaveSingleItem()
            .ShouldContain($"[{note.Id}] Use gh for GitHub.");
        calls.Single(c => c.Text == "third").Options!.ModelNotes.ShouldBeNull("each change is told once");
    }

    [Fact]
    public async Task a_harness_that_doesnt_pass_notes_on_keeps_the_notes_it_started_with()
    {
        Seed("claude-code", passesModelNotes: false);
        await _memory.SetEnabledAsync(true);
        var orchestrator = _builder.Build();

        await orchestrator.PromptSessionAsync("s1", "first", null);
        await _memory.AddAsync("machine", null, "Use gh for GitHub.");
        await orchestrator.PromptSessionAsync("s1", "second", null);

        _session.SendPromptCalls.ShouldAllBe(c => c.Options!.ModelNotes == null);
        _session.SendPromptCalls.ShouldAllBe(c => c.Options!.MemoryNotes != null, "it still takes the notes with each prompt, and keeps the first");
    }
}
