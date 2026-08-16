namespace WeaveFleet.Domain.Skills;

/// <summary>
/// The origin type of a skill installation.
/// </summary>
public enum SkillSource
{
    /// <summary>Skill bundled with the application.</summary>
    Bundled,

    /// <summary>Skill installed from a GitHub repository.</summary>
    GitHub,

    /// <summary>Skill installed from a local filesystem path.</summary>
    Local
}

/// <summary>
/// Synchronization status of a skill.
/// </summary>
public enum SkillSyncStatus
{
    /// <summary>Skill is up to date.</summary>
    UpToDate,

    /// <summary>Skill has pending updates available.</summary>
    UpdateAvailable,

    /// <summary>Skill is currently being synchronized.</summary>
    Syncing,

    /// <summary>Skill synchronization failed.</summary>
    Error
}
