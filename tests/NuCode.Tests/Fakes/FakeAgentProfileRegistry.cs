using NuCode.Agents;

namespace NuCode.Fakes;

internal sealed class FakeAgentProfileRegistry : IAgentProfileRegistry
{
    private readonly Dictionary<string, AgentProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public void Add(AgentProfile profile) => _profiles[profile.Name] = profile;

    public AgentProfile? Get(string name) => _profiles.GetValueOrDefault(name);

    public IReadOnlyList<AgentProfile> GetAll() => _profiles.Values.ToList();

    public IReadOnlyList<AgentProfile> GetVisible() =>
        _profiles.Values.Where(p => !p.IsHidden).ToList();

    public void Register(AgentProfile profile) => _profiles[profile.Name] = profile;

    public bool TryOverride(string name, Func<AgentProfile, AgentProfile> overrides)
    {
        if (!_profiles.TryGetValue(name, out var existing))
            return false;
        _profiles[name] = overrides(existing);
        return true;
    }
}
