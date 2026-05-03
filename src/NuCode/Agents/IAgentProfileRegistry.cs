namespace NuCode.Agents;

/// <summary>
/// Registry for agent profiles. Provides access to built-in and custom agent profiles.
/// </summary>
public interface IAgentProfileRegistry
{
    /// <summary>
    /// Gets an agent profile by name.
    /// </summary>
    /// <param name="name">The agent profile name (case-insensitive).</param>
    /// <returns>The agent profile, or <c>null</c> if not found.</returns>
    AgentProfile? Get(string name);

    /// <summary>
    /// Gets all registered agent profiles.
    /// </summary>
    IReadOnlyList<AgentProfile> GetAll();

    /// <summary>
    /// Gets all visible (non-hidden) agent profiles.
    /// </summary>
    IReadOnlyList<AgentProfile> GetVisible();

    /// <summary>
    /// Registers a custom agent profile. Throws if a profile with the same name already exists.
    /// </summary>
    /// <param name="profile">The agent profile to register.</param>
    void Register(AgentProfile profile);

    /// <summary>
    /// Applies overrides to an existing agent profile using a <c>with</c> expression pattern.
    /// Returns <c>true</c> if the profile was found and overridden.
    /// </summary>
    /// <param name="name">The name of the profile to override (case-insensitive).</param>
    /// <param name="overrides">
    /// A function that receives the existing profile and returns the modified profile.
    /// </param>
    /// <returns><c>true</c> if the profile was found and updated; otherwise <c>false</c>.</returns>
    bool TryOverride(string name, Func<AgentProfile, AgentProfile> overrides);
}
