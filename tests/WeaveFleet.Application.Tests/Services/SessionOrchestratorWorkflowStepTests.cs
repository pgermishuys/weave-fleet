using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// Which sessions keep the workflow step tool: a step the agent finishes does; a step the user finishes is made like
/// any other session, so the harness hides the tool from it, on spawn and on resume.
/// </summary>
public sealed class SessionOrchestratorWorkflowStepTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessRuntime _runtime;
    private readonly FakeHarnessSession _defaultSession = new("inst-1");
    private readonly SessionOrchestrator _sut;

    public ValueTask DisposeAsync() => _defaultSession.DisposeAsync();

    public SessionOrchestratorWorkflowStepTests()
    {
        _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("owner-1"));
        _builder.WorkspaceRootRepository.Seed(new WeaveFleet.Domain.Entities.WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<WeaveFleet.Domain.Entities.Instance?>(new WeaveFleet.Domain.Entities.Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        });
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true, SupportsWorkflowSteps = true });
        _runtime.DefaultSession = _defaultSession;
        _sut = _builder.Build();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task a_step_session_keeps_the_step_tool_unless_the_user_finishes_it(bool userFinishes, bool keepsTool)
    {
        using var directory = new TempDirectory();

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = directory.Path,
            Title = "Keyboard sheet · Design",
            WorkflowRunId = "run-1",
            WorkflowUserFinishes = userFinishes,
        });

        result.IsSuccess.ShouldBeTrue();
        _runtime.SpawnCalls.ShouldHaveSingleItem().WorkflowStep.ShouldBe(keepsTool);
        var saved = _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem();
        (saved.WorkflowRunId, saved.WorkflowUserFinishes).ShouldBe(("run-1", userFinishes));
    }

    [Fact]
    public async Task a_session_that_isnt_a_step_never_counts_as_one_the_user_finishes()
    {
        using var directory = new TempDirectory();

        await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = directory.Path, WorkflowUserFinishes = true });

        _runtime.SpawnCalls.ShouldHaveSingleItem().WorkflowStep.ShouldBeFalse();
        _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem().WorkflowUserFinishes.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task a_resumed_step_session_keeps_the_step_tool_unless_the_user_finishes_it(bool userFinishes, bool keepsTool)
    {
        _builder.SessionRepository.Seed(new WeaveFleet.Domain.Entities.Session
        {
            Id = "session-1",
            WorkspaceId = "workspace-1",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-1",
            Title = "Keyboard sheet · Design",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1",
            WorkflowRunId = "run-1",
            WorkflowUserFinishes = userFinishes,
        });
        _builder.WorkspaceRepository.Seed(new WeaveFleet.Domain.Entities.Workspace
        {
            Id = "workspace-1",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1",
        });

        (await _sut.ActivateSessionAsync("session-1")).IsSuccess.ShouldBeTrue();

        _runtime.ResumeCalls.ShouldHaveSingleItem().WorkflowStep.ShouldBe(keepsTool);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-workflow-steps-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
