using Shouldly;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

/// <summary>
/// A session keeps the profile it started with: it's stored on the session and handed to the harness whenever the
/// session starts, wakes, forks or gets a delegated child.
/// </summary>
public sealed class SessionOrchestratorProfileTests : IDisposable
{
    private static readonly HarnessCapabilities WithProfiles = new() { SupportsProfiles = true, SupportsResume = true };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"fleet-profile-{Guid.NewGuid():N}");
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessRuntime _runtime;
    private readonly SessionOrchestrator _sut;

    public SessionOrchestratorProfileTests()
    {
        Directory.CreateDirectory(_directory);
        _builder = new SessionOrchestratorBuilder().WithUserContext(new TestUserContext("user-1"));
        _builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = "2026-01-01" });
        _builder.ProjectRepository.Seed(new Project
        {
            Id = "scratch-1", Name = "Scratch", Type = "scratch", Position = 0, CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01",
        });
        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<Instance?>(new Instance
        {
            Id = id, Directory = "/tmp", Url = string.Empty, Status = "running", CreatedAt = "2026-01-01",
        });
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", WithProfiles);
        _runtime.DefaultSession = new FakeHarnessSession("inst-1");
        _sut = _builder.Build();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HarnessProfile SeedProfile(string id, bool isDefault = false)
    {
        var profile = new HarnessProfile
        {
            Id = id, HarnessType = "opencode", Name = id, Content = $$"""{ "model": "{{id}}/model" }""", IsDefault = isDefault, UserId = "user-1",
        };
        _builder.HarnessProfileRepository.Seed(profile);
        return profile;
    }

    [Fact]
    public async Task a_new_session_starts_with_the_profile_it_asked_for()
    {
        SeedProfile("work");

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessProfileId = "work" });

        result.IsSuccess.ShouldBeTrue();
        result.Value.Session.HarnessProfileId.ShouldBe("work");
        _runtime.PrepareCalls.Single().Profile!.Id.ShouldBe("work");
    }

    [Fact]
    public async Task without_asking_a_new_session_gets_the_default_profile()
    {
        SeedProfile("work", isDefault: true);

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory });

        result.Value.Session.HarnessProfileId.ShouldBe("work");
        _runtime.PrepareCalls.Single().Profile!.Id.ShouldBe("work");
    }

    [Fact]
    public async Task asking_for_none_skips_the_default()
    {
        SeedProfile("work", isDefault: true);

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessProfileId = HarnessProfileService.NoProfile });

        result.Value.Session.HarnessProfileId.ShouldBeNull();
        _runtime.PrepareCalls.Single().Profile.ShouldBeNull();
    }

    [Fact]
    public async Task an_unknown_profile_is_refused_before_anything_starts()
    {
        var result = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessProfileId = "nope" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.Profile");
        _runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_harness_without_profiles_refuses_one_and_ignores_the_default()
    {
        var pi = _builder.RegisterHarness("pi", "Pi");
        pi.DefaultSession = new FakeHarnessSession("inst-pi");
        SeedProfile("work", isDefault: true);

        var asked = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessType = "pi", HarnessProfileId = "work" });
        var notAsked = await _sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessType = "pi" });

        asked.IsFailure.ShouldBeTrue();
        notAsked.Value.Session.HarnessProfileId.ShouldBeNull();
    }

    [Fact]
    public async Task with_sign_in_on_profiles_are_off()
    {
        var builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("user-1"))
            .WithOptions(new FleetOptions { Auth = { Enabled = true } });
        builder.WorkspaceRootRepository.Seed(new WorkspaceRoot { Id = "root-1", Path = Path.GetTempPath(), CreatedAt = "2026-01-01" });
        builder.RegisterHarness("opencode", "OpenCode", WithProfiles);
        var sut = builder.Build();

        var result = await sut.CreateSessionAsync(new CreateSessionRequest { Directory = _directory, HarnessProfileId = "work" });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.Profile");
    }

    [Fact]
    public async Task a_session_wakes_with_its_profile_as_it_is_now()
    {
        var work = SeedProfile("work");
        SeedSession("session-1", profileId: "work");
        work.Content = """{ "model": "edited/model" }""";

        var result = await _sut.ActivateSessionAsync("session-1");

        result.IsSuccess.ShouldBeTrue();
        _runtime.PrepareCalls.Single().Profile!.Content.ShouldBe("""{ "model": "edited/model" }""");
    }

    [Fact]
    public async Task a_session_whose_profile_was_deleted_says_so_when_it_wakes()
    {
        SeedSession("session-1", profileId: "gone");

        var result = await _sut.ActivateSessionAsync("session-1");

        result.IsFailure.ShouldBeTrue();
        result.Error.Description.ShouldContain("deleted");
        _runtime.ResumeCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_fork_keeps_the_parents_profile_even_when_that_is_none()
    {
        SeedProfile("work", isDefault: true);
        SeedSession("with-profile", profileId: "work");
        SeedSession("without-profile", profileId: null);

        var forkedWith = await _sut.ForkSessionAsync("with-profile");
        var forkedWithout = await _sut.ForkSessionAsync("without-profile");

        forkedWith.Value.Session.HarnessProfileId.ShouldBe("work");
        forkedWithout.Value.Session.HarnessProfileId.ShouldBeNull();
    }

    [Fact]
    public async Task a_delegated_child_attaches_with_the_parents_profile()
    {
        SeedProfile("local");
        SeedSession("parent-1", profileId: "local");

        var result = await _sut.EnsureDelegatedChildSessionAsync("parent-1", "oc-child-1", "general");

        result.IsSuccess.ShouldBeTrue();
        result.Value.HarnessProfileId.ShouldBe("local");
        _runtime.PrepareCalls.Single().Profile!.Id.ShouldBe("local");
        _runtime.ResumeCalls.Single().LaunchArtifacts.ShouldNotBeNull();
    }

    [Fact]
    public async Task a_delegated_child_of_a_session_without_a_profile_is_prepared_like_its_parent()
    {
        // Preparation carries more than the profile (credentials, built-in skills), and the launch
        // picks the pooled process. Unprepared, the child landed on a process without its session.
        SeedSession("parent-1", profileId: null);

        var result = await _sut.EnsureDelegatedChildSessionAsync("parent-1", "oc-child-1", "general");

        result.Value.HarnessProfileId.ShouldBeNull();
        var prepare = _runtime.PrepareCalls.Single();
        prepare.Profile.ShouldBeNull();
        prepare.UserId.ShouldBe("user-1");
        prepare.ModelId.ShouldBeNull();
        var resume = _runtime.ResumeCalls.Single();
        resume.LaunchArtifacts.ShouldNotBeNull();
        resume.ParentSessionId.ShouldBe("parent-1");
    }

    private void SeedSession(string id, string? profileId)
    {
        _builder.SessionRepository.Seed(new Session
        {
            Id = id,
            WorkspaceId = "workspace-1",
            InstanceId = $"inst-{id}",
            HarnessType = "opencode",
            HarnessResumeToken = $"token-{id}",
            HarnessProfileId = profileId,
            Title = id,
            Status = "active",
            RetentionStatus = "active",
            Directory = _directory,
            CreatedAt = "2026-01-01",
            UserId = "user-1",
        });
        _builder.WorkspaceRepository.Seed(new Workspace
        {
            Id = "workspace-1", Directory = _directory, CreatedAt = "2026-01-01", UserId = "user-1",
        });
    }
}
