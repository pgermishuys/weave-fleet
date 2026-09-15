using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

public interface IHarnessProfileRepository
{
    Task<IReadOnlyList<HarnessProfile>> ListAsync(string harnessType);
    Task<HarnessProfile?> GetByIdAsync(string id);
    Task<HarnessProfile?> GetDefaultAsync(string harnessType);
    Task InsertAsync(HarnessProfile profile);
    Task<bool> UpdateAsync(HarnessProfile profile);
    Task<bool> DeleteAsync(string id);
    /// <summary>Makes <paramref name="id"/> the harness's default, or clears the default when it's null.</summary>
    Task SetDefaultAsync(string harnessType, string? id);
    /// <summary>How many sessions that aren't archived use each profile, by profile id.</summary>
    Task<IReadOnlyDictionary<string, int>> CountOpenSessionsAsync(string harnessType);
}
