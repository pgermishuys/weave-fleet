using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFleet.Api.Endpoints;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;
using WeaveFleet.Infrastructure.Plugins.BuiltIn.GitHub;

namespace WeaveFleet.Api;

// SSE/WebSocket payload envelopes
internal sealed record SseSessionEventPayload(
    string SessionId,
    string Type,
    JsonElement Payload,
    long? EventId,
    /// <summary>Deprecated compatibility alias for EventId.</summary>
    long? SequenceNumber,
    long Timestamp);

internal sealed record SseActivityEventPayload(
    string Topic,
    string Type,
    JsonElement Payload,
    long? EventId,
    /// <summary>Deprecated compatibility alias for EventId.</summary>
    long? SequenceNumber,
    long Timestamp);

internal sealed record SseSessionStoppedPayload(string SessionId, string Status);

internal sealed record WsEventDataPayload(
    string Type,
    long? EventId,
    /// <summary>Deprecated compatibility alias for EventId.</summary>
    long? SequenceNumber,
    JsonElement Properties);

internal sealed record WsEventPayload(string Type, string Topic, WsEventDataPayload Data);

internal sealed record WsSubscribedPayload(string Type, IReadOnlyList<string> Topics);

internal sealed record WsActivityStatusProperties(string SessionId, string ActivityStatus);

internal sealed record WsSnapshotPayload(string Type, string Topic, SessionSnapshot Data);

internal sealed record WsEventV2Payload(string Type, string Topic, long? EventId, DomainEvent Data);

internal sealed record WsHistoryPagePayload(IReadOnlyList<MessageLifecyclePayload> Messages, string? Cursor, bool HasMore);

internal sealed record WsHistoryPayload(string Type, string Topic, WsHistoryPagePayload Data);

internal sealed record ErrorResponse(string Error);

[JsonSerializable(typeof(SseSessionEventPayload))]
[JsonSerializable(typeof(SseActivityEventPayload))]
[JsonSerializable(typeof(SseSessionStoppedPayload))]
[JsonSerializable(typeof(WsEventDataPayload))]
[JsonSerializable(typeof(WsEventPayload))]
[JsonSerializable(typeof(WsSubscribedPayload))]
[JsonSerializable(typeof(WsActivityStatusProperties))]
[JsonSerializable(typeof(WsSnapshotPayload))]
[JsonSerializable(typeof(WsEventV2Payload))]
[JsonSerializable(typeof(WsHistoryPagePayload))]
[JsonSerializable(typeof(WsHistoryPayload))]
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(DomainEvent))]
[JsonSerializable(typeof(MessageLifecyclePayload))]
[JsonSerializable(typeof(MessagePartUpdatedPayload))]
[JsonSerializable(typeof(MessagePartDeltaStreamedPayload))]
[JsonSerializable(typeof(DelegationCreatedPayload))]
[JsonSerializable(typeof(DelegationUpdatedPayload))]
[JsonSerializable(typeof(DelegationCompletedPayload))]
[JsonSerializable(typeof(SessionDeletedPayload))]
[JsonSerializable(typeof(ErrorResponse))]
// Shared
[JsonSerializable(typeof(ApiErrorResponse))]
// Fleet
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(ProfileResponse))]
[JsonSerializable(typeof(UpdateStatusResponse))]
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
[JsonSerializable(typeof(RepositoryWorktreesResponse))]
[JsonSerializable(typeof(WorktreeItem))]
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
[JsonSerializable(typeof(List<InstanceProviderItem>))]
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
[JsonSerializable(typeof(FileDiffSummary))]
[JsonSerializable(typeof(IReadOnlyList<FileDiffSummary>))]
[JsonSerializable(typeof(List<FileDiffSummary>))]
[JsonSerializable(typeof(GetSessionStatusResponse))]
[JsonSerializable(typeof(SessionOriginRecordDto))]
[JsonSerializable(typeof(IReadOnlyList<SessionOriginRecordDto>))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.DelegationDto))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.DTOs.DelegationDto>))]
// Session Sources
[JsonSerializable(typeof(SessionSourceCatalogResponse))]
[JsonSerializable(typeof(SessionSourceItem))]
[JsonSerializable(typeof(SessionSourceKey), TypeInfoPropertyName = "ApiSessionSourceKey")]
[JsonSerializable(typeof(SessionSourceInputField))]
[JsonSerializable(typeof(WeaveFleet.Application.SessionSources.SessionSourceSelection))]
// Skills
[JsonSerializable(typeof(SkillDetailResponse))]
[JsonSerializable(typeof(SkillCreatedResponse))]
[JsonSerializable(typeof(SkillListItem))]
[JsonSerializable(typeof(SkillListItem[]))]
[JsonSerializable(typeof(IReadOnlyList<SkillListItem>))]
// Workspace Roots
[JsonSerializable(typeof(WorkspaceRootsResponse))]
[JsonSerializable(typeof(WorkspaceRootItem))]
[JsonSerializable(typeof(WorkspaceRootAddedResponse))]
// Directories
[JsonSerializable(typeof(DirectoryListingResponse))]
[JsonSerializable(typeof(DirectoryEntryResponse))]
// Boards
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.CreateBoardRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.UpdateBoardRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.CreateBoardSourceRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.UpdateBoardSourceRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.CreateBoardLaneRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.UpdateBoardLaneRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.ReorderBoardLanesRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.CreateBoardCardRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.UpdateBoardCardRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.MoveBoardCardRequest))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.BoardResponse))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.BoardLaneResponse))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.BoardSourceResponse))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.BoardCardResponse))]
[JsonSerializable(typeof(WeaveFleet.Api.Endpoints.BoardSyncResponse))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Api.Endpoints.BoardResponse>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Api.Endpoints.BoardLaneResponse>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Api.Endpoints.BoardSourceResponse>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Api.Endpoints.BoardCardResponse>))]
// Sessions (request types)
[JsonSerializable(typeof(CreateSessionApiRequest))]
[JsonSerializable(typeof(OnCompleteInfo))]
[JsonSerializable(typeof(PreviewSessionSourceApiRequest))]
[JsonSerializable(typeof(AddSessionSourceApiRequest))]
[JsonSerializable(typeof(SendPromptApiRequest))]
[JsonSerializable(typeof(ImageAttachmentDto))]
[JsonSerializable(typeof(ForkSessionApiRequest))]
[JsonSerializable(typeof(SendCommandApiRequest))]
[JsonSerializable(typeof(QuestionAnswerApiRequest))]
[JsonSerializable(typeof(ModelRef))]
// Workspace
[JsonSerializable(typeof(AddWorkspaceRootRequest))]
[JsonSerializable(typeof(RenameWorkspaceRequest))]
// User
[JsonSerializable(typeof(UserMeResponse))]
// Skills (request)
[JsonSerializable(typeof(InstallSkillRequest))]
// Auth
[JsonSerializable(typeof(AuthStatusResponse))]
[JsonSerializable(typeof(TokenLoginRequest))]
// Open Directory
[JsonSerializable(typeof(OpenDirectoryRequest))]
// Available Tools
[JsonSerializable(typeof(AvailableToolsResponse))]
[JsonSerializable(typeof(WeaveFleet.Application.Services.ResolvedTool))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Services.ResolvedTool>))]
// Telemetry
[JsonSerializable(typeof(UiActionRequest))]
// Credentials
[JsonSerializable(typeof(CredentialResponse))]
[JsonSerializable(typeof(StoreCredentialRequest))]
[JsonSerializable(typeof(UpdateCredentialRequest))]
[JsonSerializable(typeof(IReadOnlyList<CredentialResponse>))]
// Preferences
[JsonSerializable(typeof(SetPreferenceRequest))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Config
[JsonSerializable(typeof(ClientConfigResponse))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
// Application DTOs
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.UpdateSessionRetentionRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.UpdateSessionTitleRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.MoveSessionRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.SessionListResponse))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.FleetSummaryResponse))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.CreateProjectRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.UpdateProjectRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.DeleteProjectRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.ReorderProjectRequest))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.ProjectResponse))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.DTOs.ProjectResponse>))]
// GitHub plugin response types
[JsonSerializable(typeof(GitHubDeviceCodeApiResponse))]
[JsonSerializable(typeof(GitHubPollApiResponse))]
[JsonSerializable(typeof(GitHubConnectionStatusApiResponse))]
[JsonSerializable(typeof(GitHubEndpointError))]
[JsonSerializable(typeof(GitHubPollRequest))]
[JsonSerializable(typeof(GitHubConnectWithTokenRequest))]
[JsonSerializable(typeof(GitHubBookmarkRequest))]
[JsonSerializable(typeof(GitHubBookmarkSyncRequest))]
[JsonSerializable(typeof(GitHubBookmarkedRepoDto))]
[JsonSerializable(typeof(GitHubBookmarkedRepoDto[]))]
[JsonSerializable(typeof(GitHubCiStatusResponse))]
[JsonSerializable(typeof(GitHubCheckRunDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubCheckRunDto>))]
[JsonSerializable(typeof(GitHubReviewThreadsResponse))]
[JsonSerializable(typeof(GitHubReviewThreadDto))]
[JsonSerializable(typeof(GitHubReviewCommentDto))]
[JsonSerializable(typeof(IReadOnlyList<GitHubReviewThreadDto>))]
[JsonSerializable(typeof(GitHubReplyToCommentRequest))]
// Analytics
[JsonSerializable(typeof(WeaveFleet.Application.Analytics.AnalyticsSummary))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Analytics.DailyAnalytics>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Analytics.ModelAnalytics>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Analytics.SessionAnalytics>))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Analytics.TokenEventRow>))]
// ProblemDetails (used by Results.Problem())
[JsonSerializable(typeof(Microsoft.AspNetCore.Http.HttpValidationProblemDetails))]
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
// Harnesses
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.Harnesses.HarnessInfo>))]
[JsonSerializable(typeof(List<WeaveFleet.Application.Harnesses.HarnessInfo>))]
// Plugins
[JsonSerializable(typeof(PluginListResponse))]
[JsonSerializable(typeof(PluginDescriptorItem))]
[JsonSerializable(typeof(PluginStatusItem))]
[JsonSerializable(typeof(PluginActionItem))]
// Projects (List variant)
[JsonSerializable(typeof(List<WeaveFleet.Application.DTOs.ProjectResponse>))]
// List<T> variants (endpoints may return List<T> at runtime)
[JsonSerializable(typeof(List<WeaveFleet.Api.Endpoints.BoardResponse>))]
[JsonSerializable(typeof(List<WeaveFleet.Api.Endpoints.BoardLaneResponse>))]
[JsonSerializable(typeof(List<WeaveFleet.Api.Endpoints.BoardSourceResponse>))]
[JsonSerializable(typeof(List<WeaveFleet.Api.Endpoints.BoardCardResponse>))]
[JsonSerializable(typeof(List<CredentialResponse>))]
[JsonSerializable(typeof(List<WeaveFleet.Application.DTOs.SessionListResponse>))]
// SmartLinks
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.SmartLinkDto))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.DTOs.SmartLinkDto>))]
[JsonSerializable(typeof(List<WeaveFleet.Application.DTOs.SmartLinkDto>))]
[JsonSerializable(typeof(WeaveFleet.Application.DTOs.UpsertSmartLinkRequest))]
[JsonSerializable(typeof(IReadOnlyList<WeaveFleet.Application.DTOs.UpsertSmartLinkRequest>))]
[JsonSerializable(typeof(List<WeaveFleet.Application.DTOs.UpsertSmartLinkRequest>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ApiJsonContext : JsonSerializerContext
{
}
