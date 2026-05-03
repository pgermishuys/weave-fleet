using System.Collections.Concurrent;

namespace NuCode.Plugins;

/// <summary>
/// Represents a typed hook point in the NuCode lifecycle.
/// Hooks allow plugins to transform data at specific points.
/// Each hook has a specific input type (read-only context) and output type (mutable result).
/// </summary>
/// <typeparam name="TInput">The read-only input/context type passed to hook handlers.</typeparam>
/// <typeparam name="TOutput">The mutable output type that hook handlers can modify.</typeparam>
public sealed class NuCodeHook<TInput, TOutput>
{
    /// <summary>
    /// Gets the unique name of this hook.
    /// </summary>
    public string Name { get; }

    internal NuCodeHook(string name)
    {
        Name = name;
    }
}

/// <summary>
/// Delegate for a hook handler. Receives read-only input and mutable output.
/// Handlers modify the output in place; modifications accumulate across plugins.
/// </summary>
/// <typeparam name="TInput">The read-only input/context type.</typeparam>
/// <typeparam name="TOutput">The mutable output type to modify.</typeparam>
public delegate Task HookHandler<in TInput, TOutput>(TInput input, TOutput output);

/// <summary>
/// A collection of hook handlers provided by a single plugin.
/// Plugins build this via the fluent API and return it from <see cref="INuCodePlugin.Initialize"/>.
/// </summary>
public sealed class NuCodeHookCollection
{
    private readonly ConcurrentDictionary<string, List<Delegate>> _handlers = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a handler for the specified hook.
    /// </summary>
    public NuCodeHookCollection On<TInput, TOutput>(
        NuCodeHook<TInput, TOutput> hook,
        HookHandler<TInput, TOutput> handler)
    {
        var list = _handlers.GetOrAdd(hook.Name, _ => []);
        lock (list)
        {
            list.Add(handler);
        }
        return this;
    }

    /// <summary>
    /// Gets all handlers registered for the specified hook.
    /// </summary>
    internal IReadOnlyList<HookHandler<TInput, TOutput>> GetHandlers<TInput, TOutput>(NuCodeHook<TInput, TOutput> hook)
    {
        if (!_handlers.TryGetValue(hook.Name, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.Cast<HookHandler<TInput, TOutput>>().ToList();
        }
    }

    /// <summary>
    /// Gets all hook names that have handlers registered.
    /// </summary>
    internal IReadOnlyCollection<string> HookNames => [.. _handlers.Keys];
}
