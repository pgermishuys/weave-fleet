using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Repository for shadow <see cref="User"/> records.
/// </summary>
public interface IUserRepository
{
    /// <summary>Get a user by their stable identity provider ID.</summary>
    Task<User?> GetByIdAsync(string id);

    /// <summary>
    /// Insert or update a user record (upsert).
    /// Preserves existing <c>created_at</c> on update.
    /// </summary>
    Task UpsertAsync(User user);

    /// <summary>Update the <c>last_login_at</c> timestamp for the given user.</summary>
    Task UpdateLastLoginAsync(string id, string lastLoginAt);

    /// <summary>Set <c>onboarding_completed_at</c> for the given user.</summary>
    Task UpdateOnboardingCompletedAsync(string id, string completedAt);
}
