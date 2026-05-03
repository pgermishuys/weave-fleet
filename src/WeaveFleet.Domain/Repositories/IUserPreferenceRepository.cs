namespace WeaveFleet.Domain.Repositories;

/// <summary>
/// Persists user-scoped key-value preferences.
/// </summary>
public interface IUserPreferenceRepository
{
    Task<string?> GetAsync(string key);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync();
    Task SetAsync(string key, string value);
}
