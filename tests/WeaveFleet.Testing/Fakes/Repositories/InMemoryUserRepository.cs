using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly Dictionary<string, User> _store = new();

    // ── Seeding API ──────────────────────────────────────────────────────────

    public void Seed(User user) => _store[user.Id] = user;

    // ── Inspection API ───────────────────────────────────────────────────────

    public IReadOnlyList<User> All => [.. _store.Values];

    // ── IUserRepository ──────────────────────────────────────────────────────

    public Task<User?> GetByIdAsync(string id)
        => Task.FromResult(_store.GetValueOrDefault(id));

    public Task UpsertAsync(User user)
    {
        _store[user.Id] = user;
        return Task.CompletedTask;
    }

    public Task UpdateLastLoginAsync(string id, string lastLoginAt)
    {
        if (_store.TryGetValue(id, out var user))
            user.LastLoginAt = lastLoginAt;
        return Task.CompletedTask;
    }

    public Task UpdateOnboardingCompletedAsync(string id, string completedAt)
    {
        if (_store.TryGetValue(id, out var user))
            user.OnboardingCompletedAt = completedAt;
        return Task.CompletedTask;
    }
}
