using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Domain.Repositories;

/// <summary>The current user's Weave config.</summary>
public interface IWeaveConfigRepository
{
    /// <summary>The saved config, or a default one (own files, no files kept) when the user never saved one.</summary>
    Task<WeaveConfig> GetAsync();

    /// <summary>Replaces the saved source and files in one go.</summary>
    Task SaveAsync(WeaveConfig config);
}
