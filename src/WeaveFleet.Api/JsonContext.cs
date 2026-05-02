using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Api;

// SSE/WebSocket payload envelopes
internal sealed record SseSessionEventPayload(
    string SessionId,
    string Type,
    JsonElement Payload,
    long? SequenceNumber,
    long Timestamp);

internal sealed record SseActivityEventPayload(
    string Topic,
    string Type,
    JsonElement Payload,
    long? SequenceNumber,
    long Timestamp);

internal sealed record SseSessionStoppedPayload(string SessionId, string Status);

internal sealed record WsEventDataPayload(string Type, long? SequenceNumber, JsonElement Properties);

internal sealed record WsEventPayload(string Type, string Topic, WsEventDataPayload Data);

internal sealed record WsSubscribedPayload(string Type, IReadOnlyList<string> Topics);

internal sealed record WsActivityStatusProperties(string SessionId, string ActivityStatus);

internal sealed record ErrorResponse(string Error);

[JsonSerializable(typeof(SseSessionEventPayload))]
[JsonSerializable(typeof(SseActivityEventPayload))]
[JsonSerializable(typeof(SseSessionStoppedPayload))]
[JsonSerializable(typeof(WsEventDataPayload))]
[JsonSerializable(typeof(WsEventPayload))]
[JsonSerializable(typeof(WsSubscribedPayload))]
[JsonSerializable(typeof(WsActivityStatusProperties))]
[JsonSerializable(typeof(ErrorResponse))]
// Shared
[JsonSerializable(typeof(ApiErrorResponse))]
// Fleet
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(ProfileResponse))]
[JsonSerializable(typeof(RepositoriesListResponse))]
[JsonSerializable(typeof(RepositoryListItem))]
[JsonSerializable(typeof(RepositoryInfoResponse))]
[JsonSerializable(typeof(RepositoryInfoDto))]
[JsonSerializable(typeof(RepositoryLastCommit))]
[JsonSerializable(typeof(RepositoryRemote))]
[JsonSerializable(typeof(RepositoryDetailResponse))]
[JsonSerializable(typeof(RepositoryDetailDto))]
[JsonSerializable(typeof(RepositoryBranchItem))]
[JsonSerializable(typeof(RepositoryCommitItem))]
[JsonSerializable(typeof(RepositoryRemoteItem))]
[JsonSerializable(typeof(IntegrationsResponse))]
[JsonSerializable(typeof(IntegrationItem))]
// GitHub Auth
[JsonSerializable(typeof(GitHubConnectedResponse))]
[JsonSerializable(typeof(GitHubDeviceFlowResponse))]
[JsonSerializable(typeof(GitHubPollStatusResponse))]
// Instances
[JsonSerializable(typeof(InstanceProvidersResponse))]
[JsonSerializable(typeof(InstanceProviderItem))]
[JsonSerializable(typeof(InstanceModelItem))]
[JsonSerializable(typeof(InstanceCommandsResponse))]
[JsonSerializable(typeof(InstanceCommandItem))]
[JsonSerializable(typeof(InstanceAgentsResponse))]
[JsonSerializable(typeof(InstanceAgentItem))]
[JsonSerializable(typeof(InstanceAgentModelRef))]
[JsonSerializable(typeof(InstanceFilesResponse))]
// Sessions
[JsonSerializable(typeof(GetSessionResponse))]
[JsonSerializable(typeof(SessionOriginDto))]
[JsonSerializable(typeof(Session))]
[JsonSerializable(typeof(CreateSessionApiResponse))]
[JsonSerializable(typeof(PreviewSessionResponse))]
[JsonSerializable(typeof(SessionPreviewEnvelope))]
[JsonSerializable(typeof(ResumeSessionApiResponse))]
[JsonSerializable(typeof(ForkSessionApiResponse))]
[JsonSerializable(typeof(HarnessMessage))]
[JsonSerializable(typeof(IReadOnlyList<HarnessMessage>))]
[JsonSerializable(typeof(GetSessionMessagesApiResponse))]
[JsonSerializable(typeof(SessionMessagesPagination))]
[JsonSerializable(typeof(GetCommittedEventsResponse))]
[JsonSerializable(typeof(CommittedEventItem))]
[JsonSerializable(typeof(GetSessionDiffsResponse))]
[JsonSerializable(typeof(GetSessionStatusResponse))]
// Session Sources
[JsonSerializable(typeof(SessionSourceCatalogResponse))]
[JsonSerializable(typeof(SessionSourceItem))]
[JsonSerializable(typeof(SessionSourceKey))]
[JsonSerializable(typeof(SessionSourceInputField))]
// Skills
[JsonSerializable(typeof(SkillDetailResponse))]
[JsonSerializable(typeof(SkillCreatedResponse))]
[JsonSerializable(typeof(SkillListItem))]
[JsonSerializable(typeof(IReadOnlyList<SkillListItem>))]
// Workspace Roots
[JsonSerializable(typeof(WorkspaceRootsResponse))]
[JsonSerializable(typeof(WorkspaceRootItem))]
[JsonSerializable(typeof(WorkspaceRootAddedResponse))]
// GitHub plugin response types
[JsonSerializable(typeof(GitHubDeviceCodeApiResponse))]
[JsonSerializable(typeof(GitHubPollApiResponse))]
[JsonSerializable(typeof(GitHubConnectionStatusApiResponse))]
[JsonSerializable(typeof(GitHubEndpointError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
}
