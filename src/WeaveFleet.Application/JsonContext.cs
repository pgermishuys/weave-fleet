using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application;

// ── Committed-message payload types (replaces anonymous types in MessagePersistenceService) ──────

internal sealed record CommittedMessageTime(long Created);

internal sealed record CommittedMessageInfo(
    string Id,
    string Role,
    string SessionID,
    string? Agent,
    string? ModelID,
    CommittedMessageTime Time);

internal sealed record CommittedMessage(CommittedMessageInfo Info, List<JsonElement> Parts);

internal sealed record CommittedUserPromptMessage(
    CommittedMessageInfo Info,
    List<JsonElement> Parts,
    string CorrelationId);

internal sealed record CommittedTextPart(
    string Id,
    string MessageID,
    string SessionID,
    string Type,
    string Text);

internal sealed record CommittedFilePart(
    string Id,
    string MessageID,
    string SessionID,
    string Type,
    string Mime,
    string Url,
    string? Filename);

// ── Session lifecycle outbox payload types (replaces anonymous types in SessionOrchestrator) ─────

internal sealed record SessionCreatedOutboxPayload
{
    public required string SessionId { get; init; }
    public string? InstanceId { get; init; }
    public string? WorkspaceId { get; init; }
    public string? Title { get; init; }
    public string? ProjectId { get; init; }
    public string? ParentSessionId { get; init; }
    public bool? IsHidden { get; init; }
}

internal sealed record SessionStoppedOutboxPayload(string SessionId, string StoppedAt);
internal sealed record SessionArchivedOutboxPayload(string SessionId, string ArchivedAt);
internal sealed record SessionUnarchivedOutboxPayload(string SessionId);
internal sealed record SessionDeletedOutboxPayload(string SessionId);
internal sealed record ActivityStatusBroadcastPayload(string SessionId, string ActivityStatus);

// ── Session source input types (moved from private nested records so they can be source-generated) ──

/// <summary>Legacy directory input serialised into a <c>SessionSourceSelection.Input</c> payload.</summary>
internal sealed record LegacyDirectoryInput
{
    public required string Directory { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
}

/// <summary>Typed payload for the <c>local/directory/start-session</c> session source action.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DirectorySourceInput
{
    public string? Directory { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
}

// ── Source-generated serializer context ───────────────────────────────────────────────────────────

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MessagePart[]))]
[JsonSerializable(typeof(List<MessagePart>))]
[JsonSerializable(typeof(DelegationEventDto))]
[JsonSerializable(typeof(CommittedMessage))]
[JsonSerializable(typeof(CommittedUserPromptMessage))]
[JsonSerializable(typeof(CommittedTextPart))]
[JsonSerializable(typeof(CommittedFilePart))]
[JsonSerializable(typeof(SessionCreatedOutboxPayload))]
[JsonSerializable(typeof(SessionStoppedOutboxPayload))]
[JsonSerializable(typeof(SessionArchivedOutboxPayload))]
[JsonSerializable(typeof(SessionUnarchivedOutboxPayload))]
[JsonSerializable(typeof(SessionDeletedOutboxPayload))]
[JsonSerializable(typeof(ActivityStatusBroadcastPayload))]
[JsonSerializable(typeof(LegacyDirectoryInput))]
[JsonSerializable(typeof(DirectorySourceInput))]
internal sealed partial class ApplicationJsonContext : JsonSerializerContext
{
}
