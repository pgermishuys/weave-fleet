namespace NuCode.Plugins;

/// <summary>
/// Defines a NuCode plugin. Plugins provide hooks that modify behavior at specific lifecycle points.
/// </summary>
public interface INuCodePlugin
{
    /// <summary>
    /// Unique name for this plugin.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Initializes the plugin and returns the hooks it provides.
    /// Called once when the plugin is loaded.
    /// </summary>
    /// <param name="services">The application service provider for resolving dependencies.</param>
    /// <returns>A collection of hooks this plugin provides.</returns>
    NuCodeHookCollection Initialize(IServiceProvider services);
}
