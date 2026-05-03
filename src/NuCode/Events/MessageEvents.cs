namespace NuCode.Events;

/// <summary>
/// Message lifecycle events.
/// </summary>
public static class MessageEvents
{
    /// <summary>Properties for message updated events.</summary>
    public sealed record MessageInfo(SessionId SessionId, MessageId MessageId);

    /// <summary>A message was updated.</summary>
    public static readonly NuCodeEventDefinition<MessageInfo> Updated = new("message.updated");

    /// <summary>A message was removed.</summary>
    public static readonly NuCodeEventDefinition<MessageInfo> Removed = new("message.removed");

    /// <summary>Properties for message part updated events.</summary>
    public sealed record PartInfo(SessionId SessionId, MessageId MessageId, PartId PartId);

    /// <summary>A message part was updated.</summary>
    public static readonly NuCodeEventDefinition<PartInfo> PartUpdated = new("message.part.updated");

    /// <summary>Properties for message part delta (streaming) events.</summary>
    public sealed record PartDelta(SessionId SessionId, MessageId MessageId, PartId PartId, string Field, string Delta);

    /// <summary>An incremental delta was received for a message part.</summary>
    public static readonly NuCodeEventDefinition<PartDelta> PartDeltaReceived = new("message.part.delta");

    /// <summary>A message part was removed.</summary>
    public static readonly NuCodeEventDefinition<PartInfo> PartRemoved = new("message.part.removed");
}
