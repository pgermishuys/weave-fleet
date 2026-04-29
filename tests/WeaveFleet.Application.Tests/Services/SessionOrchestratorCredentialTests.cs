using Shouldly;
using WeaveFleet.Application.Harnesses;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Testing.Builders;
using WeaveFleet.Testing.Fakes;

namespace WeaveFleet.Application.Tests.Services;

public sealed class SessionOrchestratorCredentialTests : IAsyncDisposable
{
    private readonly SessionOrchestratorBuilder _builder;
    private readonly FakeHarnessRuntime _runtime;
    private readonly FakeHarnessSession _defaultSession = new("inst-1");
    private readonly SessionOrchestrator _sut;

    public ValueTask DisposeAsync() => _defaultSession.DisposeAsync();

    public SessionOrchestratorCredentialTests()
    {
        _builder = new SessionOrchestratorBuilder()
            .WithUserContext(new TestUserContext("participant-1"));

        _builder.WorkspaceRootRepository.Seed(new WeaveFleet.Domain.Entities.WorkspaceRoot
        {
            Id = "root-1",
            Path = Path.GetTempPath(),
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        _builder.InstanceRepository.GetByIdBehavior = id => Task.FromResult<WeaveFleet.Domain.Entities.Instance?>(new WeaveFleet.Domain.Entities.Instance
        {
            Id = id,
            Port = 0,
            Directory = "/tmp",
            Url = string.Empty,
            Status = "running",
            CreatedAt = DateTime.UtcNow.ToString("O")
        });

        // Register opencode harness with SupportsResume = true by default
        _runtime = _builder.RegisterHarness("opencode", "OpenCode", new HarnessCapabilities { SupportsResume = true });
        _runtime.DefaultSession = _defaultSession;

        _builder.ProjectRepository.Seed(new WeaveFleet.Domain.Entities.Project
        {
            Id = "scratch-1",
            Name = "Scratch",
            Type = "scratch",
            Position = 0,
            CreatedAt = "2026-01-01",
            UpdatedAt = "2026-01-01"
        });

        _sut = _builder.Build();
    }

    [Fact]
    public async Task CreateSessionAsync_LoadsCurrentUserCredentialsAndPassesThemToPrepareRuntimeAsync()
    {
        using var tempDirectory = new TempDirectory();
        var credentials = new List<UserCredential>
        {
            CreateCredential("participant-1", "anthropic", "api-key", "secret-1")
        };
        RuntimePreparationContext? capturedContext = null;

        // Use behavior override to return the exact same list reference for ShouldBeSameAs assertion
        _builder.CredentialStore.GetDecryptedCredentialsBehavior = _ =>
            Task.FromResult<IReadOnlyList<UserCredential>>(credentials);
        _runtime.PrepareRuntimeBehavior = (context, _) =>
        {
            capturedContext = context;
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(new StubLaunchArtifacts()));
        };

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path,
            Title = "Credentialed Session"
        });

        result.IsSuccess.ShouldBeTrue();
        _builder.CredentialStore.GetDecryptedCredentialsCalls.ShouldContain("participant-1");
        capturedContext.ShouldNotBeNull();
        capturedContext.UserId.ShouldBe("participant-1");
        capturedContext.UserCredentials.ShouldBeSameAs(credentials);
        capturedContext.ModelId.ShouldBeNull();
        capturedContext.WorkingDirectory.ShouldBe(WorkspaceRootService.CanonicalizePath(tempDirectory.Path));
    }

    [Fact]
    public async Task CreateSessionAsync_WhenPrepareRuntimeReturnsNotReady_ReturnsValidationFailureWithoutSpawning()
    {
        using var tempDirectory = new TempDirectory();

        _runtime.PreparationResult = new RuntimePreparation.NotReady([
            new RuntimePreparationError(
                "MissingCredential",
                "An Anthropic API key is required to use this model.",
                "Add an API key in Settings → Credentials")
        ]);

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.NotReady");
        result.Error.Description.ShouldBe("An Anthropic API key is required to use this model.");
        _runtime.SpawnCalls.ShouldBeEmpty();
        _builder.SessionRepository.InsertedSessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateSessionAsync_WhenPrepareRuntimeReturnsReady_PassesArtifactsThroughToSpawnAsync()
    {
        using var tempDirectory = new TempDirectory();
        var artifacts = new StubLaunchArtifacts();

        _runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts));

        var result = await _sut.CreateSessionAsync(new CreateSessionRequest
        {
            Directory = tempDirectory.Path
        });

        result.IsSuccess.ShouldBeTrue();
        _runtime.SpawnCalls.Count.ShouldBe(1);
        ReferenceEquals(_runtime.SpawnCalls[0].LaunchArtifacts, artifacts).ShouldBeTrue();
    }

    [Fact]
    public async Task ResumeSessionAsync_LoadsOwnerCredentialsAndPassesArtifactsToResumeAsync()
    {
        var ownerCredentials = new List<UserCredential>
        {
            CreateCredential("owner-1", "anthropic", "api-key", "owner-secret")
        };
        var artifacts = new StubLaunchArtifacts();
        RuntimePreparationContext? capturedContext = null;

        _builder.SessionRepository.Seed(new WeaveFleet.Domain.Entities.Session
        {
            Id = "session-1",
            WorkspaceId = "workspace-1",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-1",
            Title = "Resume Me",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1"
        });
        _builder.WorkspaceRepository.Seed(new WeaveFleet.Domain.Entities.Workspace
        {
            Id = "workspace-1",
            Directory = "/tmp/workspace",
            CreatedAt = "2026-01-01",
            UserId = "owner-1"
        });
        // Use behavior override to return the exact same list reference for ShouldBeSameAs assertion
        _builder.CredentialStore.GetDecryptedCredentialsBehavior = _ =>
            Task.FromResult<IReadOnlyList<UserCredential>>(ownerCredentials);
        _runtime.PrepareRuntimeBehavior = (context, _) =>
        {
            capturedContext = context;
            return Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts));
        };

        var result = await _sut.ResumeSessionAsync("session-1");

        result.IsSuccess.ShouldBeTrue();
        _builder.CredentialStore.GetDecryptedCredentialsCalls.ShouldContain("owner-1");
        capturedContext.ShouldNotBeNull();
        capturedContext.UserId.ShouldBe("owner-1");
        capturedContext.UserCredentials.ShouldBeSameAs(ownerCredentials);
        capturedContext.WorkingDirectory.ShouldBe("/tmp/workspace");
        _runtime.ResumeCalls.Count.ShouldBe(1);
        _runtime.ResumeCalls[0].OwnerUserId.ShouldBe("owner-1");
        _runtime.ResumeCalls[0].ResumeToken.ShouldBe("resume-token-1");
        ReferenceEquals(_runtime.ResumeCalls[0].LaunchArtifacts, artifacts).ShouldBeTrue();
        _runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenPrepareRuntimeReturnsNotReady_DoesNotResumeOrSpawn()
    {
        _builder.SessionRepository.Seed(new WeaveFleet.Domain.Entities.Session
        {
            Id = "session-2",
            WorkspaceId = "workspace-2",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-2",
            Title = "Blocked Resume",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace-2",
            CreatedAt = "2026-01-01",
            UserId = "owner-2"
        });
        _builder.WorkspaceRepository.Seed(new WeaveFleet.Domain.Entities.Workspace
        {
            Id = "workspace-2",
            Directory = "/tmp/workspace-2",
            CreatedAt = "2026-01-01",
            UserId = "owner-2"
        });
        _runtime.PreparationResult = new RuntimePreparation.NotReady([
            new RuntimePreparationError("MissingCredential", "Add credentials before resuming.")
        ]);

        var result = await _sut.ResumeSessionAsync("session-2");

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Validation.Session.NotReady");
        result.Error.Description.ShouldBe("Add credentials before resuming.");
        _runtime.ResumeCalls.ShouldBeEmpty();
        _runtime.SpawnCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResumeSessionAsync_WhenResumeIsUnsupported_PassesArtifactsThroughToSpawnAsync()
    {
        var artifacts = new StubLaunchArtifacts();

        _builder.SessionRepository.Seed(new WeaveFleet.Domain.Entities.Session
        {
            Id = "session-3",
            WorkspaceId = "workspace-3",
            InstanceId = "inst-old",
            HarnessType = "opencode",
            HarnessResumeToken = "resume-token-3",
            Title = "Fallback Resume",
            Status = "active",
            RetentionStatus = "active",
            Directory = "/tmp/workspace-3",
            CreatedAt = "2026-01-01",
            UserId = "owner-3"
        });
        _builder.WorkspaceRepository.Seed(new WeaveFleet.Domain.Entities.Workspace
        {
            Id = "workspace-3",
            Directory = "/tmp/workspace-3",
            CreatedAt = "2026-01-01",
            UserId = "owner-3"
        });
        // Override capabilities to SupportsResume = false
        var harness = (FakeHarness)_builder.HarnessRegistry.GetByType("opencode")!;
        harness.Capabilities = new HarnessCapabilities { SupportsResume = false };
        _runtime.PrepareRuntimeBehavior = (_, _) =>
            Task.FromResult<RuntimePreparation>(new RuntimePreparation.Ready(artifacts));

        var result = await _sut.ResumeSessionAsync("session-3");

        result.IsSuccess.ShouldBeTrue();
        _runtime.SpawnCalls.Count.ShouldBe(1);
        _runtime.SpawnCalls[0].OwnerUserId.ShouldBe("owner-3");
        ReferenceEquals(_runtime.SpawnCalls[0].LaunchArtifacts, artifacts).ShouldBeTrue();
        _runtime.ResumeCalls.ShouldBeEmpty();
    }

    private static UserCredential CreateCredential(string userId, string credentialNamespace, string kind, string decryptedValue)
    {
        var timestamp = DateTime.UtcNow.ToString("O");
        return new UserCredential
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Namespace = credentialNamespace,
            Kind = kind,
            Label = $"{credentialNamespace}-{kind}-{Guid.NewGuid():N}",
            EncryptedValue = decryptedValue,
            DisplayHint = decryptedValue.Length >= 4 ? decryptedValue[^4..] : decryptedValue,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fleet-credentials-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed record StubLaunchArtifacts : RuntimeLaunchArtifacts;
}
