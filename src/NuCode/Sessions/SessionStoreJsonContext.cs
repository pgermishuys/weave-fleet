using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NuCode.Permissions;

namespace NuCode.Sessions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserMessage))]
[JsonSerializable(typeof(AssistantMessage))]
[JsonSerializable(typeof(TextPart))]
[JsonSerializable(typeof(ReasoningPart))]
[JsonSerializable(typeof(ToolPart))]
[JsonSerializable(typeof(FilePart))]
[JsonSerializable(typeof(SnapshotPart))]
[JsonSerializable(typeof(PatchPart))]
[JsonSerializable(typeof(AgentPart))]
[JsonSerializable(typeof(CompactionPart))]
[JsonSerializable(typeof(SubtaskPart))]
[JsonSerializable(typeof(RetryPart))]
[JsonSerializable(typeof(StepStartPart))]
[JsonSerializable(typeof(StepFinishPart))]
[JsonSerializable(typeof(PendingToolCallState))]
[JsonSerializable(typeof(RunningToolCallState))]
[JsonSerializable(typeof(CompletedToolCallState))]
[JsonSerializable(typeof(ErrorToolCallState))]
[JsonSerializable(typeof(ImmutableArray<FileDiff>))]
[JsonSerializable(typeof(SessionRevert))]
[JsonSerializable(typeof(PermissionRuleset))]
[JsonSerializable(typeof(ProviderAuthError))]
[JsonSerializable(typeof(OutputLengthError))]
[JsonSerializable(typeof(AbortedError))]
[JsonSerializable(typeof(ContextOverflowError))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(UnknownMessageError))]
internal sealed partial class SessionStoreJsonContext : JsonSerializerContext;
