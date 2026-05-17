namespace NuCode.Configuration;

/// <summary>
/// Loads and merges NuCode configuration from multiple layers (global, project, programmatic).
/// </summary>
internal interface IConfigLoader
{
    /// <summary>
    /// Loads the merged configuration from all layers.
    /// Layer priority (lowest to highest): global config → project config → programmatic overrides.
    /// </summary>
    NuCodeConfig Load();
}
