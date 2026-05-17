namespace NuCode.Tools;

/// <summary>
/// Resolves skill names to their content. Consumers can implement this interface
/// to provide custom skill discovery and loading.
/// </summary>
public interface ISkillProvider
{
    /// <summary>
    /// Gets the content of a skill by name.
    /// </summary>
    /// <param name="name">The skill name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The skill content, or null if the skill is not found.</returns>
    Task<string?> GetSkillContentAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available skill names.
    /// </summary>
    IReadOnlyList<string> GetAvailableSkills();
}
