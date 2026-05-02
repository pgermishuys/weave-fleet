using System.Text.Json;
using WeaveFleet.Application.DTOs;
using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Api.Endpoints;

// ── Shared ─────────────────────────────────────────────────────────────────

/// <summary>General-purpose error envelope used in 4xx/5xx responses.</summary>
public sealed record ApiErrorResponse(string Error);

// ── Version / Profile ───────────────────────────────────────────────────────

public sealed record VersionResponse(string Version, string Commit);
public sealed record ProfileResponse(string Profile);

// ── Repositories ────────────────────────────────────────────────────────────

public sealed record RepositoriesListResponse(
    IReadOnlyList<RepositoryListItem> Repositories,
    long ScannedAt);

public sealed record RepositoryListItem(string Path, string Name, string ParentRoot);

public sealed record RepositoryInfoResponse(RepositoryInfoDto Repository);

public sealed record RepositoryInfoDto(
    string Name,
    string Path,
    string? Branch,
    RepositoryLastCommit? LastCommit,
    IReadOnlyList<RepositoryRemote> Remotes);

public sealed record RepositoryLastCommit(string Hash, string Message, string Author, string Date);

public sealed record RepositoryRemote(string Name, string Url);

public sealed record RepositoryDetailResponse(RepositoryDetailDto Repository);

public sealed record RepositoryDetailDto(
    string Name,
    string Path,
    string? Branch,
    int UncommittedCount,
    int TotalCommitCount,
    string? FirstCommitDate,
    string? LastCommitDate,
    IReadOnlyList<RepositoryBranchItem> Branches,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RepositoryCommitItem> RecentCommits,
    IReadOnlyList<RepositoryRemoteItem> Remotes,
    string? ReadmeContent,
    string? ReadmeFilename);

public sealed record RepositoryBranchItem(
    string Name,
    string ShortHash,
    string Message,
    string Author,
    string AuthorEmail,
    string Date,
    bool IsCurrent,
    bool IsRemote);

public sealed record RepositoryCommitItem(
    string Hash,
    string ShortHash,
    string Message,
    string Author,
    string AuthorEmail,
    string Date);

public sealed record RepositoryRemoteItem(string Name, string Url, string? Github);

// ── Integrations ────────────────────────────────────────────────────────────

public sealed record IntegrationsResponse(IReadOnlyList<IntegrationItem> Integrations);

public sealed record IntegrationItem(
    string Id,
    string Name,
    string Status,
    DateTimeOffset? ConnectedAt);

// ── GitHub Auth ─────────────────────────────────────────────────────────────

public sealed record GitHubConnectedResponse(bool Connected);

public sealed record GitHubDeviceFlowResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

public sealed record GitHubPollStatusResponse(string Status, int? Interval, string? Message);

// ── Instances ───────────────────────────────────────────────────────────────

public sealed record InstanceProvidersResponse(IReadOnlyList<InstanceProviderItem> Providers);

public sealed record InstanceProviderItem(
    string Id,
    string Name,
    IReadOnlyList<InstanceModelItem> Models);

public sealed record InstanceModelItem(string Id, string Name);

public sealed record InstanceCommandsResponse(
    string InstanceId,
    IReadOnlyList<InstanceCommandItem> Commands);

public sealed record InstanceCommandItem(string Name, string? Description);

public sealed record InstanceAgentsResponse(
    string InstanceId,
    IReadOnlyList<InstanceAgentItem> Agents);

public sealed record InstanceAgentItem(
    string Name,
    string? Description,
    string Mode,
    bool Hidden,
    InstanceAgentModelRef? Model);

public sealed record InstanceAgentModelRef(string ProviderID, string ModelID);

public sealed record InstanceFilesResponse(
    string InstanceId,
    IReadOnlyList<string> Files);

// ── Sessions ─────────────────────────────────────────────────────────────────

public sealed record GetSessionResponse(
    string Id,
    string? InstanceId,
    string WorkspaceId,
    string WorkspaceDirectory,
    string? WorkspaceDisplayName,
    string? SourceDirectory,
    string IsolationStrategy,
    string? Branch,
    string? Title,
    string CreatedAt,
    string? StoppedAt,
    string? ActivityStatus,
    string? LifecycleStatus,
    string? RetentionStatus,
    string? ArchivedAt,
    int? TotalTokens,
    double? TotalCost,
    string? HarnessType,
    string? ProjectId,
    SessionOriginDto? Origin);

public sealed record CreateSessionApiResponse(string InstanceId, string WorkspaceId, Session Session);

public sealed record PreviewSessionResponse(SessionPreviewEnvelope Preview);

public sealed record SessionPreviewEnvelope(
    string OriginLabel,
    string Content,
    bool IsTruncated,
    int CharacterCount);

public sealed record ResumeSessionApiResponse(string InstanceId, Session Session);

public sealed record ForkSessionApiResponse(
    string InstanceId,
    string WorkspaceId,
    Session Session,
    string ForkedFromSessionId);

public sealed record GetSessionMessagesApiResponse(
    IReadOnlyList<HarnessMessage> Messages,
    SessionMessagesPagination Pagination);

public sealed record SessionMessagesPagination(
    bool HasMore,
    string? OldestMessageId,
    int TotalCount);

public sealed record GetCommittedEventsResponse(
    IReadOnlyList<CommittedEventItem> Events);

public sealed record CommittedEventItem(
    long SequenceNumber,
    string Topic,
    string Type,
    JsonElement Payload,
    long Timestamp);

public sealed record GetSessionDiffsResponse(IReadOnlyList<JsonElement> Diffs);

public sealed record GetSessionStatusResponse(
    string? Status,
    string? ActivityStatus,
    string LifecycleStatus,
    string? RetentionStatus,
    string? ArchivedAt);

// ── Session Sources ──────────────────────────────────────────────────────────

public sealed record SessionSourceCatalogResponse(
    IReadOnlyList<SessionSourceItem> Sources);

public sealed record SessionSourceItem(
    SessionSourceKey Key,
    string DisplayName,
    string Kind,
    IReadOnlyList<SessionSourceInputField> InputFields,
    bool ProducesWorkspace,
    bool ProducesContext,
    bool RequiresConfirmation);

public sealed record SessionSourceKey(
    string ProviderId,
    string SourceType,
    string ActionId,
    int ContractVersion);

public sealed record SessionSourceInputField(
    string Name,
    string ValueType,
    bool Required,
    IReadOnlyList<string>? AllowedValues,
    string? Description);

// ── Skills ──────────────────────────────────────────────────────────────────

public sealed record SkillDetailResponse(string Name, string Path, string? Prompt);
public sealed record SkillCreatedResponse(string Name, string Path);
public sealed record SkillListItem(string Name, string Path, bool HasPrompt);

// ── Workspace Roots ──────────────────────────────────────────────────────────

public sealed record WorkspaceRootsResponse(IReadOnlyList<WorkspaceRootItem> Roots);

public sealed record WorkspaceRootItem(string? Id, string Path, string Source, bool Exists);

public sealed record WorkspaceRootAddedResponse(string Id, string Path);
