namespace NuCode.Events;

/// <summary>
/// Base type for all NuCode events. Events carry a type discriminator and typed properties.
/// </summary>
/// <param name="Type">The event type identifier (e.g., "session.created", "permission.asked").</param>
public abstract record NuCodeEvent(string Type);

/// <summary>
/// A NuCode event with typed properties.
/// </summary>
/// <typeparam name="TProperties">The type of the event properties.</typeparam>
/// <param name="Type">The event type identifier.</param>
/// <param name="Properties">The event-specific payload.</param>
public sealed record NuCodeEvent<TProperties>(string Type, TProperties Properties)
    : NuCodeEvent(Type);
