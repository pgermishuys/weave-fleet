using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

public sealed class InMemoryUserPreferenceRepository : IUserPreferenceRepository
{
    private readonly Dictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(string key, string value) => _store[key] = value;

    public Task<string?> GetAsync(string key)
        => Task.FromResult(_store.GetValueOrDefault(key));

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync()
    {
        IReadOnlyDictionary<string, string> result = new Dictionary<string, string>(_store);
        return Task.FromResult(result);
    }

    public Task SetAsync(string key, string value)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }
}
