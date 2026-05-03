namespace NuCode.Events;

/// <summary>
/// Global event bus singleton for cross-session event propagation.
/// Wraps an <see cref="INuCodeEventBus"/> instance and delegates all operations to it.
/// Registered as a singleton in DI. Session-scoped buses can forward events here.
/// </summary>
internal sealed class GlobalEventBus : INuCodeEventBus
{
    private readonly NuCodeEventBus _inner = new();

    public void Publish<TProperties>(NuCodeEventDefinition<TProperties> definition, TProperties properties) =>
        _inner.Publish(definition, properties);

    public IDisposable Subscribe<TProperties>(NuCodeEventDefinition<TProperties> definition, Action<NuCodeEvent<TProperties>> callback) =>
        _inner.Subscribe(definition, callback);

    public IDisposable SubscribeAll(Action<NuCodeEvent> callback) =>
        _inner.SubscribeAll(callback);
}
