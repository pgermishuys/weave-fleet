namespace WeaveFleet.Domain.Entities;

/// <summary>
/// A GitHub pull request or issue attached to a session, enriched with live status.
/// </summary>
public sealed class SmartLink
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public bool IsDismissed { get; set; }
    public bool IsTerminal { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    /// <summary>Owner's user identifier.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>How the link relates to its session. See <see cref="SmartLinkRelationships"/>.</summary>
    public string Relationship { get; set; } = SmartLinkRelationships.Mentioned;

    /// <summary>Whether live details were fetched. See <see cref="SmartLinkEnrichmentStatuses"/>.</summary>
    public string EnrichmentStatus { get; set; } = SmartLinkEnrichmentStatuses.Pending;

    /// <summary>When the provider was last asked for this link's status.</summary>
    public string? LastCheckedAt { get; set; }
}

/// <summary>How a smart link relates to its session.</summary>
public static class SmartLinkRelationships
{
    /// <summary>The issue or pull request the session was started from.</summary>
    public const string Origin = "origin";

    /// <summary>A pull request the session created, or whose head branch is the session's branch.</summary>
    public const string Own = "own";

    /// <summary>A link the user attached or promoted.</summary>
    public const string Pinned = "pinned";

    /// <summary>A link found in the conversation.</summary>
    public const string Mentioned = "mentioned";

    /// <summary>
    /// Ranks relationships so detection only ever upgrades one: a link first seen as
    /// mentioned can become own, but an origin never drops back to mentioned.
    /// </summary>
    public static int Rank(string relationship) => relationship switch
    {
        Origin => 3,
        Own => 2,
        Pinned => 1,
        _ => 0,
    };
}

/// <summary>Whether a smart link's live details were fetched from its provider.</summary>
public static class SmartLinkEnrichmentStatuses
{
    public const string Pending = "pending";
    public const string Resolved = "resolved";
    public const string NotConnected = "not_connected";
    public const string NotFound = "not_found";
    public const string Error = "error";
}
