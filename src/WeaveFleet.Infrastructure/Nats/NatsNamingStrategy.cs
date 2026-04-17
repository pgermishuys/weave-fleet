using System.Buffers;
using WeaveFleet.Application.Configuration;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Nats;

/// <summary>
/// Subject / stream / consumer name construction for the NATS event substrate.
/// Keeps tenant/project/session hierarchy out of every other component.
/// <para>
/// <c>{sid}</c> in every subject is the <b>Fleet</b> session id (globally unique across harnesses),
/// not the harness-provider session id that may also live on <c>HarnessEvent.SessionId</c>.
/// </para>
/// </summary>
public sealed class NatsNamingStrategy
{
    public const string ScratchProjectSentinel = "scratch";

    // NATS subject tokens are separated by '.' and support '*' / '>' wildcards. Any segment
    // we interpolate into a subject must be validated so an attacker cannot forge a routing
    // hierarchy or escape tenant scoping via a project/session id containing these characters.
    private static readonly SearchValues<char> UnsafeSubjectChars = SearchValues.Create(['.', '*', '>', ' ', '\t', '\r', '\n']);

    private readonly NatsOptions _options;
    private readonly string _nodeId;

    public NatsNamingStrategy(NatsOptions options, string nodeId)
    {
        _options = options;
        _nodeId = string.IsNullOrWhiteSpace(nodeId)
            ? throw new ArgumentException("NodeId is required.", nameof(nodeId))
            : nodeId;
    }

    /// <summary>
    /// Construct the single subject for a harness event. All events — durable and ephemeral —
    /// publish to this subject tree; the durable/ephemeral distinction is expressed by which
    /// NATS API the publisher uses (JetStream for durable, core NATS for ephemeral), not by
    /// subject hierarchy. JetStream captures durable events via its <see cref="DurableStreamSubjects"/>
    /// filter; the core NATS fan-out subscription sees everything.
    /// </summary>
    public string Subject(string? projectId, string sessionId, string eventType)
    {
        var project = projectId ?? ScratchProjectSentinel;
        ValidateSegment(project, nameof(projectId));
        ValidateSegment(sessionId, nameof(sessionId));
        return $"{_options.TenantPrefix}.project.{project}.session.{sessionId}.{eventType}";
    }

    /// <summary>
    /// Wildcard subject used by the unified fan-out subscriber on core NATS and by the
    /// <c>FilterSubject</c> on durable JetStream consumers.
    /// </summary>
    public static string FanOutSubscriptionFilter => "tenant.*.project.*.session.*.>";

    /// <summary>
    /// Enumerated list of durable leaf-subject patterns the <c>FLEET_EVENTS</c> JetStream stream
    /// captures. Ephemeral event subjects (<c>message.part.delta</c>, <c>session.status</c>,
    /// <c>session.idle</c>, <c>error</c>, <c>permission.*</c>, <c>server.*</c>) are deliberately
    /// absent so the stream only stores events that require at-least-once SQLite persistence.
    /// Keep in sync with <c>EventTypes</c> + <c>EventTypeMetadata</c>.
    /// </summary>
    public static IReadOnlyList<string> DurableStreamSubjects { get; } =
    [
        "tenant.*.project.*.session.*." + EventTypes.MessageCreated,
        "tenant.*.project.*.session.*." + EventTypes.MessageUpdated,
        "tenant.*.project.*.session.*." + EventTypes.MessagePartUpdated,
        "tenant.*.project.*.session.*." + EventTypes.MessageRemoved,
        "tenant.*.project.*.session.*." + EventTypes.MessagePartRemoved,
        "tenant.*.project.*.session.*." + EventTypes.SessionUpdated,
        "tenant.*.project.*.session.*." + EventTypes.SessionError,
        "tenant.*.project.*.session.*." + EventTypes.SessionCompacted,
        "tenant.*.project.*.session.*." + EventTypes.SessionDeleted,
    ];

    public string StreamName => _options.StreamName;

    /// <summary>
    /// Cluster-scoped consumer: one consumer shared across all Fleet nodes. Used by projections
    /// that must write exactly once per event (e.g. persistence).
    /// </summary>
    public string ClusterConsumerName(string projection) => $"{_options.StreamName}-{projection}";

    /// <summary>
    /// Per-node consumer: one consumer per Fleet node. Used by fan-out projections (e.g.
    /// WebSocket broadcast) where every node must receive its own copy of the stream.
    /// </summary>
    public string PerNodeConsumerName(string projection) => $"{_options.StreamName}-{projection}-{_nodeId}";

    public readonly record struct ParsedSubject(string Tenant, string ProjectId, string SessionId, string EventType);

    /// <summary>
    /// Parse <c>tenant.{ws}.project.{pid}.session.{sid}.{evt.Type}</c> back to its parts.
    /// The event type is the remainder after the <c>session.{sid}.</c> segment (may contain dots).
    /// Returns null for malformed input.
    /// </summary>
    public static ParsedSubject? ParseSubject(string subject)
    {
        var parts = subject.Split('.');
        // Minimum: tenant.{ws}.project.{pid}.session.{sid}.{type} = 7 tokens
        if (parts.Length < 7) return null;
        if (parts[0] != "tenant" || parts[2] != "project" || parts[4] != "session") return null;
        var type = string.Join('.', parts[6..]);
        return new ParsedSubject(parts[1], parts[3], parts[5], type);
    }

    private static void ValidateSegment(string segment, string paramName)
    {
        if (string.IsNullOrEmpty(segment))
            throw new ArgumentException("Subject segment cannot be empty.", paramName);
        if (segment.AsSpan().IndexOfAny(UnsafeSubjectChars) >= 0)
            throw new ArgumentException(
                $"Subject segment '{segment}' contains a character reserved by NATS (., *, >, whitespace).",
                paramName);
    }
}
