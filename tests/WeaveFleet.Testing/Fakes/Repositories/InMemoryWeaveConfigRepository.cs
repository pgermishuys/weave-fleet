using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Testing.Fakes.Repositories;

/// <summary>In-memory implementation of <see cref="IWeaveConfigRepository"/> for one user.</summary>
public sealed class InMemoryWeaveConfigRepository : IWeaveConfigRepository
{
    /// <summary>What's saved; null until the first save.</summary>
    public WeaveConfig? Saved { get; private set; }

    public Task<WeaveConfig> GetAsync() => Task.FromResult(Saved is null
        ? new WeaveConfig()
        : new WeaveConfig
        {
            Source = Saved.Source,
            Files = new Dictionary<string, string>(Saved.Files, StringComparer.Ordinal),
            UpdatedAt = Saved.UpdatedAt,
        });

    public Task SaveAsync(WeaveConfig config)
    {
        Saved = new WeaveConfig
        {
            Source = config.Source,
            Files = new Dictionary<string, string>(config.Files, StringComparer.Ordinal),
            UpdatedAt = config.UpdatedAt,
        };
        return Task.CompletedTask;
    }
}
