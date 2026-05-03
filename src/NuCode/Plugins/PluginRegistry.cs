using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NuCode.Plugins;

/// <summary>
/// Default implementation of <see cref="IPluginRegistry"/>.
/// Manages plugin registration, initialization, and hook triggering.
/// </summary>
internal sealed class PluginRegistry : IPluginRegistry
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PluginRegistry> _logger;
    private readonly object _lock = new();
    private readonly List<INuCodePlugin> _plugins = [];
    private readonly List<NuCodeHookCollection> _hookCollections = [];

    internal PluginRegistry(IServiceProvider services, ILoggerFactory? loggerFactory)
    {
        _services = services;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<PluginRegistry>();
    }

    public void Register(INuCodePlugin plugin)
    {
        lock (_lock)
        {
            if (_plugins.Any(p => string.Equals(p.Name, plugin.Name, StringComparison.Ordinal)))
            {
                _logger.LogWarning("Plugin '{PluginName}' is already registered; skipping duplicate.", plugin.Name);
                return;
            }

            try
            {
                var hooks = plugin.Initialize(_services);
                _plugins.Add(plugin);
                _hookCollections.Add(hooks);
                _logger.LogInformation("Plugin '{PluginName}' registered with hooks: {Hooks}",
                    plugin.Name, string.Join(", ", hooks.HookNames));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize plugin '{PluginName}'; it will not be registered.", plugin.Name);
            }
        }
    }

    public IReadOnlyList<INuCodePlugin> GetAll()
    {
        lock (_lock)
        {
            return [.. _plugins];
        }
    }

    public async Task<TOutput> TriggerAsync<TInput, TOutput>(
        NuCodeHook<TInput, TOutput> hook,
        TInput input,
        TOutput output)
    {
        List<NuCodeHookCollection> snapshot;
        lock (_lock)
        {
            snapshot = [.. _hookCollections];
        }

        foreach (var collection in snapshot)
        {
            var handlers = collection.GetHandlers(hook);
            foreach (var handler in handlers)
            {
                try
                {
                    await handler(input, output).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hook handler for '{HookName}' threw an exception; continuing with next handler.", hook.Name);
                }
            }
        }

        return output;
    }
}
