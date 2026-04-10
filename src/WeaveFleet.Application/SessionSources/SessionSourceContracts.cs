using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeaveFleet.Application.SessionSources;

public static class SessionSourceKinds
{
    public const string Workspace = "workspace";
    public const string Context = "context";
    public const string Hybrid = "hybrid";
}

public static class SessionSourceActions
{
    public const string StartSession = "start-session";
    public const string AddToSession = "add-to-session";
}

public static class SessionSourceProviderIds
{
    public const string Local = "builtin.local";
    public const string Managed = "builtin.managed";
    public const string Repository = "builtin.repository";
    public const string GitHub = "builtin.github";
}

public static class SessionSourceTypeNames
{
    public const string Directory = "directory";
    public const string ManagedWorkspace = "managed-workspace";
    public const string Repository = "repository";
    public const string ExternalDocument = "external-document";
    public const string GitHubIssue = "github-issue";
    public const string GitHubPullRequest = "github-pull-request";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionSourceKey
{
    public required string ProviderId { get; init; }
    public required string SourceType { get; init; }
    public required string ActionId { get; init; }
    public int ContractVersion { get; init; } = 1;
}

public sealed record SessionSourceInputField(
    string Name,
    string ValueType,
    bool Required,
    IReadOnlyList<string>? AllowedValues,
    string? Description);

public sealed record SessionSourceDescriptor(
    SessionSourceKey Key,
    string DisplayName,
    string Kind,
    IReadOnlyList<SessionSourceInputField> InputFields,
    bool ProducesWorkspace,
    bool ProducesContext,
    bool RequiresConfirmation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionSourceSelection
{
    public required SessionSourceKey Key { get; init; }
    public required JsonElement Input { get; init; }
}

public sealed record WorkspaceIntent(
    string Directory,
    string IsolationStrategy,
    string? Branch);

public sealed record ContextEnvelope(
    string OriginLabel,
    string Content,
    bool IsTruncated,
    int CharacterCount);

public sealed record ProvenanceRecord(
    string ProviderId,
    string SourceType,
    string ActionId,
    string? ResourceId,
    string? ResourceUrl,
    string? Title,
    string? Summary,
    string ResolvedAt);

public sealed record ResolvedSessionInput(
    WorkspaceIntent? WorkspaceIntent,
    ContextEnvelope? ContextEnvelope,
    ProvenanceRecord Provenance);

public sealed record ResolvedSessionSource(
    SessionSourceDescriptor Descriptor,
    ResolvedSessionInput Input);

public static class SessionSourceCatalog
{
    public static SessionSourceDescriptor DirectoryStartSession { get; } = new(
        new SessionSourceKey
        {
            ProviderId = SessionSourceProviderIds.Local,
            SourceType = SessionSourceTypeNames.Directory,
            ActionId = SessionSourceActions.StartSession,
            ContractVersion = 1
        },
        "Directory",
        SessionSourceKinds.Workspace,
        [
            new SessionSourceInputField("directory", "string", true, null, "Canonical local directory path."),
            new SessionSourceInputField("isolationStrategy", "string", false, ["existing", "worktree", "clone"], "Workspace isolation mode."),
            new SessionSourceInputField("branch", "string", false, null, "Optional branch for isolated workspaces.")
        ],
        ProducesWorkspace: true,
        ProducesContext: false,
        RequiresConfirmation: false);

    public static SessionSourceDescriptor RepositoryStartSession { get; } = new(
        new SessionSourceKey
        {
            ProviderId = SessionSourceProviderIds.Repository,
            SourceType = SessionSourceTypeNames.Repository,
            ActionId = SessionSourceActions.StartSession,
            ContractVersion = 1
        },
        "Repository",
        SessionSourceKinds.Workspace,
        [
            new SessionSourceInputField("repositoryPath", "string", true, null, "Canonical repository directory path."),
            new SessionSourceInputField("isolationStrategy", "string", false, ["existing", "worktree"], "Repository workspace isolation mode."),
            new SessionSourceInputField("branch", "string", false, null, "Optional branch for isolated workspaces.")
        ],
        ProducesWorkspace: true,
        ProducesContext: false,
        RequiresConfirmation: false);

    public static SessionSourceDescriptor ManagedWorkspaceStartSession { get; } = new(
        new SessionSourceKey
        {
            ProviderId = SessionSourceProviderIds.Managed,
            SourceType = SessionSourceTypeNames.ManagedWorkspace,
            ActionId = SessionSourceActions.StartSession,
            ContractVersion = 1
        },
        "Managed workspace",
        SessionSourceKinds.Workspace,
        [],
        ProducesWorkspace: true,
        ProducesContext: false,
        RequiresConfirmation: false);

    public static SessionSourceDescriptor ExternalDocumentAddToSession { get; } = new(
        new SessionSourceKey
        {
            ProviderId = "provider.external",
            SourceType = SessionSourceTypeNames.ExternalDocument,
            ActionId = SessionSourceActions.AddToSession,
            ContractVersion = 1
        },
        "External document",
        SessionSourceKinds.Context,
        [
            new SessionSourceInputField("resourceId", "string", true, null, "Stable provider resource id."),
            new SessionSourceInputField("selection", "string", false, null, "Optional provider-specific selection scope.")
        ],
        ProducesWorkspace: false,
        ProducesContext: true,
        RequiresConfirmation: true);

    public static IReadOnlyList<SessionSourceDescriptor> CoreDescriptors { get; } =
    [
        DirectoryStartSession,
        ManagedWorkspaceStartSession,
        RepositoryStartSession,
        ExternalDocumentAddToSession
    ];
}
