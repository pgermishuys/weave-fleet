using Shouldly;
using WeaveFleet.Application.SessionSources;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Workflows;

/// <summary>
/// The project a run's step session asks for: the run's chosen project, until the run has a session of its own to
/// say where it actually lives (even one whose prompt failed), falling back to Scratch if that project was deleted
/// before the run got one.
/// </summary>
public sealed class WorkflowStepSessionsTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessSession _defaultSession = new("inst-1");
    private readonly WorkflowStepSessions _sut;

    public ValueTask DisposeAsync() => _defaultSession.DisposeAsync();

    public WorkflowStepSessionsTests()
    {
        _builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"))
            .WithSessionSourceProvider(new FakeRepositorySessionSourceProvider());
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot
        {
            Id = "root-1", Path = Path.GetTempPath(), CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id, Port = 0, Directory = "/tmp", Url = string.Empty, Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        var runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsWorkflowSteps = true });
        runtime.DefaultSession = _defaultSession;
        _builder.ProjectRepository.Seed(
            new Project { Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" },
            new Project { Id = "proj-1", Name = "Dependencies", Type = "standard", Position = 1, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" },
            new Project { Id = "proj-2", Name = "Other", Type = "standard", Position = 2, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" });
        _builder.Build();
        _sut = new WorkflowStepSessions(_builder.Creation, _builder.Prompting, new FakeSessionMessageProxy(), _builder.SessionRepository, _builder.ProjectRepository);
    }

    private static WorkflowAgentStep Step(string id = "plan") => new(
        Id: id, Title: "Plan", Line: 1, Agent: null, Model: "fake/standard", Effort: null, Skill: null,
        Optional: false, OptionalHint: null, Prompt: "Plan it.", Outcomes: ["ready"],
        Routes: new Dictionary<string, string>(), MaxLoops: null, Finish: null, Writes: []);

    private static WorkflowRun NewRun(string? projectId, string? worktreePath = null) => new()
    {
        Id = "run-1", UserId = "user-1", WorkflowId = "builtin:x", WorkflowName = "X", Definition = "",
        Request = "Bump deps", Slug = "bump-deps", Title = "Bump deps", RepositoryPath = Path.GetTempPath(),
        BaseBranch = "main", WorktreePath = worktreePath, HarnessType = "opencode", ProjectId = projectId,
        CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
    };

    [Fact]
    public async Task the_first_step_session_is_created_in_the_runs_chosen_project()
    {
        var run = NewRun(projectId: "proj-1");

        var started = await _sut.StartAsync(run, Step(), "Plan it.", WorkflowModelChoice.Default, userFinishes: false, CancellationToken.None);

        started.Error.ShouldBeNull();
        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        saved.ProjectId.ShouldBe("proj-1");
    }

    [Fact]
    public async Task with_no_project_chosen_the_first_step_session_goes_to_scratch()
    {
        var run = NewRun(projectId: null);

        await _sut.StartAsync(run, Step(), "Plan it.", WorkflowModelChoice.Default, userFinishes: false, CancellationToken.None);

        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        saved.ProjectId.ShouldBe("scratch-1");
    }

    [Fact]
    public async Task a_later_step_follows_the_run_to_wherever_it_was_moved_not_the_project_chosen_at_start()
    {
        // The run started in proj-1, but its (only) session so far was then moved to proj-2.
        _builder.SessionRepository.Seed(new Session
        {
            Id = "plan-session", WorkspaceId = "w1", InstanceId = "i1", OpencodeSessionId = "oc-plan",
            Title = "Bump deps · Plan", Status = "idle", Directory = Path.GetTempPath(),
            CreatedAt = "2026-01-01T00:00:00.0000000Z", WorkflowRunId = "run-1", ProjectId = "proj-2", UserId = "user-1",
        });
        var run = NewRun(projectId: "proj-1", worktreePath: "/repo-worktrees/bump-deps");

        await _sut.StartAsync(run, Step("implement"), "Implement it.", WorkflowModelChoice.Default, userFinishes: false, CancellationToken.None);

        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        saved.ProjectId.ShouldBe("proj-2");
    }

    [Fact]
    public async Task a_session_exists_even_without_a_worktree_so_a_later_step_still_follows_its_move()
    {
        // The first step's session was created (so the run has one), but its prompt failed before the worktree's
        // directory came back, so run.WorktreePath is still null — the bug this guards against.
        _builder.SessionRepository.Seed(new Session
        {
            Id = "plan-session", WorkspaceId = "w1", InstanceId = "i1", OpencodeSessionId = "oc-plan",
            Title = "Bump deps · Plan", Status = "idle", Directory = Path.GetTempPath(),
            CreatedAt = "2026-01-01T00:00:00.0000000Z", WorkflowRunId = "run-1", ProjectId = "proj-2", UserId = "user-1",
        });
        var run = NewRun(projectId: "proj-1", worktreePath: null);

        await _sut.StartAsync(run, Step("implement"), "Implement it.", WorkflowModelChoice.Default, userFinishes: false, CancellationToken.None);

        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        saved.ProjectId.ShouldBe("proj-2");
    }

    [Fact]
    public async Task a_chosen_project_deleted_before_the_run_got_a_session_falls_back_to_scratch()
    {
        // You step or a slow start can leave a run with no session yet while its chosen project is deleted.
        await _builder.ProjectRepository.DeleteAsync("proj-1");
        var run = NewRun(projectId: "proj-1");

        await _sut.StartAsync(run, Step(), "Plan it.", WorkflowModelChoice.Default, userFinishes: false, CancellationToken.None);

        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        saved.ProjectId.ShouldBe("scratch-1");
    }

    /// <summary>Resolves <c>builtin.repository</c> to the repository path itself, as an existing directory: no real
    /// git worktree is needed to prove where the created session's project ends up.</summary>
    private sealed class FakeRepositorySessionSourceProvider : ISessionSourceProvider
    {
        public string ProviderId => SessionSourceProviderIds.Repository;

        public IReadOnlyList<SessionSourceDescriptor> GetDescriptors() => [SessionSourceCatalog.RepositoryStartSession];

        public Task<Result<ResolvedSessionSource>> ResolveAsync(SessionSourceSelection selection, CancellationToken cancellationToken)
        {
            var input = selection.Input;
            var directory = input.TryGetProperty("existingWorktreePath", out var existing)
                ? existing.GetString()!
                : input.GetProperty("repositoryPath").GetString()!;

            var resolved = new ResolvedSessionSource(
                SessionSourceCatalog.RepositoryStartSession,
                new ResolvedSessionInput(
                    new WorkspaceIntent(directory, "existing", null),
                    null,
                    new ProvenanceRecord(
                        ProviderId, SessionSourceTypeNames.Repository, SessionSourceActions.StartSession,
                        ResourceId: null, ResourceUrl: null, Title: null, Summary: null,
                        ResolvedAt: DateTime.UtcNow.ToString("O"))));
            return Task.FromResult<Result<ResolvedSessionSource>>(resolved);
        }
    }
}
