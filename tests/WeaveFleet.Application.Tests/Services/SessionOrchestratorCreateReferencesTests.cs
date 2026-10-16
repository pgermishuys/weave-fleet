using Shouldly;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Sessions.Creation;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// The sessions the first message references: expanded into the prompt the harness gets, and a bad reference refuses the
/// create before a workspace, process or session row exists.
/// </summary>
public sealed class SessionOrchestratorCreateReferencesTests : IDisposable
{
    private const string Typed = "Use the notes in @t3code-notes";
    private static readonly SessionReference Reference = new("@t3code-notes", "ses-ref");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-create-refs-{Guid.NewGuid():N}");
    private readonly SessionOrchestratorBuilder _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
    private readonly FakeHarnessRuntime _runtime;

    public SessionOrchestratorCreateReferencesTests()
    {
        Directory.CreateDirectory(_directory);
        // RequiresInitialPrompt hands the first message to the spawn, where the test can read it.
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsFleetTools = true, RequiresInitialPrompt = true });
        _builder.RegisterHarness("claude-code", "Claude Code", new HarnessCapabilities { RequiresInitialPrompt = true });
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = "2026-01-01" });
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
        _builder.SessionRepository.Seed(new Session
        {
            Id = "ses-ref", InstanceId = "inst-ref", Title = "t3code notes", Status = "active", Directory = _directory,
            CreatedAt = "2026-01-01", RetentionStatus = "active", HarnessType = "opencode", UserId = "user-1",
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task a_harness_with_fleet_tools_gets_the_typed_text_and_a_link_block()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Notes", InitialPrompt = Typed, SessionReferences = [Reference],
        });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        var prompt = _runtime.SpawnCalls.ShouldHaveSingleItem().InitialPrompt!;
        prompt.ShouldStartWith(Typed + "\n\n<fleet-session-references>\n" + SessionReferences.LinkNote);
        prompt.ShouldContain("<session ref=\"@t3code-notes\" id=\"ses-ref\" title=\"t3code notes\" />");
        result.Value.Session.Title.ShouldBe("Notes");
    }

    [Fact]
    public async Task a_harness_without_fleet_tools_gets_a_recap_block()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Notes", HarnessType = "claude-code", InitialPrompt = Typed, SessionReferences = [Reference],
        });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        var prompt = _builder.HarnessRegistry.GetRuntimeByType("claude-code").ShouldBeOfType<FakeHarnessRuntime>().SpawnCalls.ShouldHaveSingleItem().InitialPrompt!;
        prompt.ShouldContain(SessionReferences.RecapNote);
    }

    [Fact]
    public async Task a_reference_to_a_session_that_is_not_there_is_refused_before_anything_is_created()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = _directory, Title = "Notes", InitialPrompt = "Look at @gone", SessionReferences = [new SessionReference("@gone", "ses-gone")],
        });

        result.Error.Code.ShouldBe("Validation.SessionReferences");
        _builder.WorkspaceRepository.InsertedWorkspaces.ShouldBeEmpty();
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.InstanceRepository.All.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task without_references_the_prompt_is_as_typed()
    {
        var sut = _builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, Title = "Notes", InitialPrompt = Typed });

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Description : null);
        _runtime.SpawnCalls.ShouldHaveSingleItem().InitialPrompt.ShouldBe(Typed);
    }
}
