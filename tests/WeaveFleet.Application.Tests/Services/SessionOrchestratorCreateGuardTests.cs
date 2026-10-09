using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// What <see cref="SessionOrchestrator.CreateSessionAsync"/> refuses: a caller's folder in cloud mode, an agent too far
/// down a chain of agent-started sessions, and a completion callback to a session that isn't there.
/// </summary>
public sealed class SessionOrchestratorCreateGuardTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-create-guard-{Guid.NewGuid():N}");
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessRuntime _runtime;

    public SessionOrchestratorCreateGuardTests()
    {
        Directory.CreateDirectory(_directory);
        _runtime = _builder.RegisterHarness("opencode", "OpenCode");
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = "2026-01-01" });
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void SeedSpawnChain(int length)
    {
        string? spawnedBy = null;
        for (var i = 0; i < length; i++)
        {
            _builder.SessionRepository.Seed(new Session
            {
                Id = $"chain-{i}", InstanceId = $"inst-chain-{i}", Title = $"Chain {i}", Status = "active", Directory = _directory,
                CreatedAt = "2026-01-01", RetentionStatus = "active", HarnessType = "opencode", UserId = "user-1", SpawnedBySessionId = spawnedBy,
            });
            spawnedBy = $"chain-{i}";
        }
    }

    [Fact]
    public async Task in_cloud_mode_a_folder_the_caller_names_is_refused_before_anything_starts()
    {
        var sut = _builder.WithOptions(new FleetOptions { Cloud = new CloudOptions { Enabled = true, WorkspaceRoot = _directory } }).Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, Title = "Rate limit headers" });

        result.Error.Code.ShouldBe("Validation.Directory");
        result.Error.Description.ShouldBe("Arbitrary directory paths are not allowed in cloud mode. Managed workspaces are created automatically.");
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_agent_as_deep_as_fleet_allows_cannot_start_another_session()
    {
        // chain-0 is the user's; chain-3 is three agent-starts below it.
        SeedSpawnChain(SessionLineage.MaxAgentSpawnDepth + 1);
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Too deep", SpawnedBySessionId = $"chain-{SessionLineage.MaxAgentSpawnDepth}",
        });

        result.Error.ShouldBe(SessionLineage.TooDeep(SessionLineage.MaxAgentSpawnDepth));
        _runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_agent_one_level_less_deep_starts_a_session_marked_as_started_by_it()
    {
        SeedSpawnChain(SessionLineage.MaxAgentSpawnDepth);
        var sut = _builder.Build();
        var spawnedBy = $"chain-{SessionLineage.MaxAgentSpawnDepth - 1}";

        var result = await sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, Title = "Deep enough", SpawnedBySessionId = spawnedBy });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        result.Value.Session.SpawnedBySessionId.ShouldBe(spawnedBy);
        result.Value.Session.SpawnKind.ShouldBe(SpawnKinds.Api);
    }

    [Fact]
    public async Task a_callback_to_a_session_that_is_not_there_fails_the_create_after_the_session_was_made()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "With callback", OnCompleteTargetSessionId = "sess-gone", OnCompleteTargetInstanceId = "inst-gone",
        });

        result.Error.Code.ShouldEndWith(".NotFound");
        // As it is today: the session and its harness already exist when the target is checked.
        _builder.SessionRepository.InsertedSessions.ShouldHaveSingleItem().Title.ShouldBe("With callback");
        _runtime.SpawnCalls.ShouldHaveSingleItem();
        _builder.SessionCallbackRepository.All.ShouldBeEmpty();
    }
}
