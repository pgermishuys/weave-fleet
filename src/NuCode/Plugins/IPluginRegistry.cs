namespace NuCode.Plugins;

/// <summary>
/// Registry for NuCode plugins. Manages plugin lifecycle and hook execution.
/// </summary>
internal interface IPluginRegistry
{
    /// <summary>
    /// Registers a plugin instance. The plugin is initialized immediately.
    /// </summary>
    /// <param name="plugin">The plugin to register.</param>
    void Register(INuCodePlugin plugin);

    /// <summary>
    /// Gets all registered plugins.
    /// </summary>
    IReadOnlyList<INuCodePlugin> GetAll();

    /// <summary>
    /// Triggers a hook across all registered plugins, running handlers sequentially.
    /// Each handler can mutate the output; mutations accumulate across plugins.
    /// Individual handler failures are logged but do not prevent subsequent handlers from running.
    /// </summary>
    /// <typeparam name="TInput">The read-only input type.</typeparam>
    /// <typeparam name="TOutput">The mutable output type.</typeparam>
    /// <param name="hook">The hook to trigger.</param>
    /// <param name="input">The read-only input data.</param>
    /// <param name="output">The mutable output data that handlers modify.</param>
    /// <returns>The (possibly modified) output.</returns>
    Task<TOutput> TriggerAsync<TInput, TOutput>(NuCodeHook<TInput, TOutput> hook, TInput input, TOutput output);
}
