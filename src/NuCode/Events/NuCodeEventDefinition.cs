namespace NuCode.Events;

/// <summary>
/// Defines an event type with its string key and property type.
/// Used as a static descriptor for publish/subscribe operations.
/// </summary>
/// <typeparam name="TProperties">The type of the event properties.</typeparam>
/// <param name="Type">The event type identifier.</param>
public sealed record NuCodeEventDefinition<TProperties>(string Type)
{
    /// <summary>
    /// Creates an event instance with the given properties.
    /// </summary>
    public NuCodeEvent<TProperties> Create(TProperties properties) => new(Type, properties);
}
