using System.Collections.Concurrent;

namespace NuCode.Agents;

/// <summary>
/// Default implementation of <see cref="IAgentProfileRegistry"/>.
/// Pre-populated with built-in agent profiles; supports custom registrations and overrides.
/// </summary>
internal sealed class AgentProfileRegistry : IAgentProfileRegistry
{
    private readonly ConcurrentDictionary<string, AgentProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public AgentProfileRegistry()
    {
        foreach (var profile in BuiltInAgents.GetAll())
        {
            _profiles[profile.Name] = profile;
        }
    }

    public AgentProfile? Get(string name) =>
        _profiles.GetValueOrDefault(name);

    public IReadOnlyList<AgentProfile> GetAll() =>
        _profiles.Values.ToList().AsReadOnly();

    public IReadOnlyList<AgentProfile> GetVisible() =>
        _profiles.Values.Where(p => !p.IsHidden).ToList().AsReadOnly();

    public void Register(AgentProfile profile)
    {
        if (!_profiles.TryAdd(profile.Name, profile))
        {
            throw new InvalidOperationException(
                $"An agent profile with name '{profile.Name}' is already registered. Use {nameof(TryOverride)} to modify existing profiles.");
        }
    }

    public bool TryOverride(string name, Func<AgentProfile, AgentProfile> overrides)
    {
        if (!_profiles.TryGetValue(name, out var existing))
        {
            return false;
        }

        var updated = overrides(existing);
        _profiles[name] = updated;
        return true;
    }
}
