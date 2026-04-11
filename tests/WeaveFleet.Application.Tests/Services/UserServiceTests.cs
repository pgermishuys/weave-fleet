using NSubstitute;
using Shouldly;
using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Tests.Services;

public sealed class UserServiceTests
{
    private readonly IUserRepository _userRepository = Substitute.For<IUserRepository>();
    private readonly ICredentialStore _credentialStore = Substitute.For<ICredentialStore>();
    private readonly ISessionRepository _sessionRepository = Substitute.For<ISessionRepository>();
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
        _credentialStore.ListCredentialsAsync().Returns([
            new CredentialSummary("cred-1", "Work Key", "anthropic", "api-key", "****1234", null, "2026-01-01", "2026-01-01")
        ]);
        _sessionRepository.ListAsync(1, 0, null, null).Returns([
            new Session
            {
                Id = "session-1",
                WorkspaceId = "workspace-1",
                InstanceId = "instance-1",
                OpencodeSessionId = "opencode-1",
                Title = "Started",
                Directory = "/tmp",
                CreatedAt = "2026-01-01"
            }
        ]);

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
