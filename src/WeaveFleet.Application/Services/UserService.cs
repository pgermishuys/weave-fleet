using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Synchronizes authenticated principals into shadow <see cref="User"/> records.
/// </summary>
public sealed class UserService(
    IUserRepository userRepository,
    ICredentialStore credentialStore,
    ISessionRepository sessionRepository)
{
    public async Task<User?> EnsureUserAsync(IUserContext userContext)
    {
        if (!userContext.IsAuthenticated)
            return null;

        var existingUser = await userRepository.GetByIdAsync(userContext.UserId);
        var now = DateTime.UtcNow.ToString("O");

        if (existingUser is null)
        {
            var newUser = new User
            {
                Id = userContext.UserId,
                Email = userContext.Email ?? string.Empty,
                DisplayName = userContext.DisplayName,
                Status = "active",
                CreatedAt = now,
                LastLoginAt = now
            };

            await userRepository.UpsertAsync(newUser);
            return newUser;
        }

        existingUser.Email = userContext.Email ?? existingUser.Email;
        existingUser.DisplayName = userContext.DisplayName ?? existingUser.DisplayName;
        existingUser.LastLoginAt = now;

        await userRepository.UpsertAsync(existingUser);
        return existingUser;
    }

    public async Task CompleteOnboardingAsync(IUserContext userContext)
    {
        var user = await userRepository.GetByIdAsync(userContext.UserId);
        if (user is null)
            return;

        if (user.OnboardingCompletedAt is null)
        {
            var completedAt = DateTime.UtcNow.ToString("O");
            user.OnboardingCompletedAt = completedAt;
            await userRepository.UpdateOnboardingCompletedAsync(userContext.UserId, completedAt);
        }
    }

    public async Task<UserOnboardingStatus> GetOnboardingStatusAsync(User? user)
    {
        if (user is null)
        {
            return new UserOnboardingStatus(
                Completed: false,
                HasStoredCredentials: false,
                HasCreatedSession: false);
        }

        var credentials = await credentialStore.ListCredentialsAsync();
        var sessions = await sessionRepository.ListAsync(limit: 1, offset: 0);

        return new UserOnboardingStatus(
            Completed: user.OnboardingCompletedAt is not null,
            HasStoredCredentials: credentials.Count > 0,
            HasCreatedSession: sessions.Count > 0);
    }
}

public sealed record UserOnboardingStatus(
    bool Completed,
    bool HasStoredCredentials,
    bool HasCreatedSession);
