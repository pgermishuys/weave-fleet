namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A conversation session between the user and an opencode instance, optionally linked to a project.
/// </summary>
public sealed class Session
{
    public string Id { get; set; } = string.Empty;
    public string WorkspaceId { get; set; } = string.Empty;
    public string InstanceId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string OpencodeSessionId { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled";
    public string Status { get; set; } = "active";
    public string Directory { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? StoppedAt { get; set; }
    public string? ParentSessionId { get; set; }
    public string? ActivityStatus { get; set; }
    public string? LifecycleStatus { get; set; }
    public string RetentionStatus { get; set; } = "active";
    public string? ArchivedAt { get; set; }
    public bool IsHidden { get; set; }
    public int TotalTokens { get; set; }
    public double TotalCost { get; set; }
    public string HarnessType { get; set; } = "opencode";
    public string? HarnessResumeToken { get; set; }
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Last model selection used on this session. Persisted on every successful prompt that
    /// carried an explicit model so the next prompt can fall back to it when the SPA omits
    /// one (e.g. after a refresh wipes client-side state).
    /// </summary>
    public string? SelectedProviderId { get; set; }
    /// <inheritdoc cref="SelectedProviderId" />
    public string? SelectedModelId { get; set; }

    /// <summary>
    /// Discriminates between session views: <c>"v1"</c> (workspace-grouped) or <c>"v2"</c> (project-grouped).
    /// All sessions default to <c>"v2"</c>.
    /// </summary>
    public string ViewMode { get; set; } = "v2";
}
