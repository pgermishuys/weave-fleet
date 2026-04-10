using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Synchronizes authenticated principals into shadow <see cref="User"/> records.
/// </summary>
public sealed class UserService(IUserRepository userRepository)
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
}
