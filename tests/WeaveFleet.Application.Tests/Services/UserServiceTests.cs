using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Testing.Fakes;
using WeaveFleet.Testing.Fakes.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class UserServiceTests
{
    private readonly InMemoryUserRepository _userRepository = new();
    private readonly FakeCredentialStore _credentialStore = new();
    private readonly InMemorySessionRepository _sessionRepository = new();
    private readonly UserService _sut;

    public UserServiceTests()
    {
        _sut = new UserService(_userRepository, _credentialStore, _sessionRepository);
    }

    [Fact]
    public async Task GetOnboardingStatusAsync_WhenUserMissing_ReturnsAllFalse()
    {
        var result = await _sut.GetOnboardingStatusAsync(null);

        result.Completed.ShouldBeFalse();
        result.HasStoredCredentials.ShouldBeFalse();
        result.HasCreatedSession.ShouldBeFalse();
    }

    [Fact]
    public async Task GetOnboardingStatusAsync_WhenUserHasCompletedCredentialsAndSession_ReturnsAllTrue()
    {
        _credentialStore.Seed(new UserCredential
        {
            Id = "cred-1",
            UserId = "user-1",
            Label = "Work Key",
            Namespace = "anthropic",
            Kind = "api-key",
            EncryptedValue = "ENC:secret",
            DisplayHint = "...1234",
            CreatedAt = "2026-01-01",
            UpdatedAt = "2026-01-01"
        });
        _sessionRepository.Seed(new Session
        {
            Id = "session-1",
            WorkspaceId = "workspace-1",
            InstanceId = "instance-1",
            OpencodeSessionId = "opencode-1",
            Title = "Started",
            Directory = "/tmp",
            CreatedAt = "2026-01-01"
        });

        var result = await _sut.GetOnboardingStatusAsync(new User
        {
            Id = "user-1",
            Email = "test@example.com",
            CreatedAt = "2026-01-01",
            OnboardingCompletedAt = "2026-01-02"
        });

        result.Completed.ShouldBeTrue();
        result.HasStoredCredentials.ShouldBeTrue();
        result.HasCreatedSession.ShouldBeTrue();
    }
}
