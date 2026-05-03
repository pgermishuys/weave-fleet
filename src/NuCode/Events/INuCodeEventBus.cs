namespace NuCode.Events;

/// <summary>
/// Publishes and subscribes to NuCode events within a scope (session or global).
/// </summary>
public interface INuCodeEventBus
{
    /// <summary>
    /// Publishes a typed event to all matching subscribers.
    /// </summary>
    /// <typeparam name="TProperties">The event properties type.</typeparam>
    /// <param name="definition">The event definition.</param>
    /// <param name="properties">The event properties.</param>
    void Publish<TProperties>(NuCodeEventDefinition<TProperties> definition, TProperties properties);

    /// <summary>
    /// Subscribes to a specific event type. Returns a disposable that unsubscribes when disposed.
    /// </summary>
    /// <typeparam name="TProperties">The event properties type.</typeparam>
    /// <param name="definition">The event definition to subscribe to.</param>
    /// <param name="callback">The callback invoked when the event is published.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    IDisposable Subscribe<TProperties>(NuCodeEventDefinition<TProperties> definition, Action<NuCodeEvent<TProperties>> callback);

    /// <summary>
    /// Subscribes to all events regardless of type.
    /// </summary>
    /// <param name="callback">The callback invoked for every published event.</param>
    /// <returns>An <see cref="IDisposable"/> that removes the subscription when disposed.</returns>
    IDisposable SubscribeAll(Action<NuCodeEvent> callback);
}
