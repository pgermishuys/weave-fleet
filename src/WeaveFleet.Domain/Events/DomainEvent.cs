using System.Text.Json.Serialization;

namespace WeaveFleet.Domain.Events;

/// <summary>
/// Base type for all strongly typed domain events emitted by Weave Fleet.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStarted), "session.started")]
[JsonDerivedType(typeof(SessionIdled), "session.idled")]
[JsonDerivedType(typeof(SessionStopped), "session.stopped")]
[JsonDerivedType(typeof(SessionDeleted), "session.deleted")]
[JsonDerivedType(typeof(SessionArchived), "session.archived")]
[JsonDerivedType(typeof(TurnStarted), "turn.started")]
[JsonDerivedType(typeof(TurnEnded), "turn.ended")]
[JsonDerivedType(typeof(MessageCreated), "message.created")]
[JsonDerivedType(typeof(MessageUpdated), "message.updated")]
[JsonDerivedType(typeof(MessagePartUpdated), "message.part.updated")]
[JsonDerivedType(typeof(MessagePartDeltaStreamed), "message.part.delta.streamed")]
[JsonDerivedType(typeof(DelegationCreated), "delegation.created")]
[JsonDerivedType(typeof(DelegationUpdated), "delegation.updated")]
[JsonDerivedType(typeof(DelegationCompleted), "delegation.completed")]
public abstract record DomainEvent;
