using System.Text.Json.Serialization;

namespace NuCode.Configuration;

/// <summary>
/// Configuration for a skill entry. Maps a skill name to a file path.
/// </summary>
public sealed class SkillConfig
{
    /// <summary>
    /// The file path to the skill's SKILL.md file.
    /// Can be absolute or relative to the working directory.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Optional description of the skill for display purposes.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}
