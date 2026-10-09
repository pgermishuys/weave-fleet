using WeaveFleet.Application.SessionSources;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Sessions.Creation;

/// <summary>Input for creating a new session.</summary>
public sealed record CreateSessionRequest
{
    public string? Directory { get; init; }
    public string? Title { get; init; }
    public string? IsolationStrategy { get; init; }
    public string? Branch { get; init; }
    public string? HarnessType { get; init; }
    /// <summary>
    /// The profile to start the session with. Null means the harness's default profile, if it has one;
    /// <see cref="HarnessProfileService.NoProfile"/> means none.
    /// </summary>
    public string? HarnessProfileId { get; init; }
    public string? ProjectId { get; init; }
    public string? InitialPrompt { get; init; }
    public SessionSourceSelection? Source { get; init; }
    /// <summary>If set, registers a completion callback to resume this target session.</summary>
    public string? OnCompleteTargetSessionId { get; init; }
    public string? OnCompleteTargetInstanceId { get; init; }
    /// <summary>
    /// Optional beta-tester scenario id. Only honoured when fleet runs with --harness=test;
    /// production harnesses ignore it. The orchestrator passes it through to
    /// <see cref="HarnessSpawnOptions.ScenarioId"/> at spawn time.
    /// </summary>
    public string? ScenarioId { get; init; }
    /// <summary>
    /// When true, the request originates from an internal orchestrator operation (e.g. fork)
    /// and directory-path validation is bypassed. Must not be set from external API requests.
    /// </summary>
    internal bool IsInternalRequest { get; init; }
    /// <summary>
    /// Optional automation reference. When set, links this session to an automation execution.
    /// </summary>
    public string? SourceReference { get; init; }
    /// <summary>
    /// Optional tags for categorizing and filtering sessions.
    /// </summary>
    public List<string>? Tags { get; init; }
    /// <summary>
    /// The agent the session starts with; null for the harness's default. Prompts that name no agent get it too.
    /// </summary>
    public string? Agent { get; init; }
    /// <summary>
    /// The model the session starts with, used only with <see cref="ModelId"/>; null for the agent's or the
    /// harness's default. Prompts that name no model get it too.
    /// </summary>
    public string? ProviderId { get; init; }
    /// <inheritdoc cref="ProviderId" />
    public string? ModelId { get; init; }
    /// <summary>
    /// The workflow run the session is a step of. Set only by the workflow runner: the session keeps the step tool,
    /// which every other session has hidden.
    /// </summary>
    internal string? WorkflowRunId { get; init; }
    /// <summary>
    /// The workflow step is one the user finishes: the session is made like any session that isn't a step, with the
    /// step tool hidden, so only the user can end it.
    /// </summary>
    internal bool WorkflowUserFinishes { get; init; }
    /// <summary>What a new worktree's branch is named from, when it isn't <see cref="InitialPrompt"/>.</summary>
    internal string? BranchNamingText { get; init; }
    /// <summary>
    /// The session whose agent asked for this one (<see cref="Session.SpawnedBySessionId"/>). Set only once Fleet knows
    /// who called, never from a request's body.
    /// </summary>
    public string? SpawnedBySessionId { get; init; }
    /// <summary>
    /// How the session came to be (<see cref="SpawnKinds"/>). Defaults to <see cref="SpawnKinds.Workflow"/> for a
    /// workflow step and <see cref="SpawnKinds.Api"/> when <see cref="SpawnedBySessionId"/> is set.
    /// </summary>
    public string? SpawnKind { get; init; }
}

/// <summary>Result of a successful <see cref="SessionCreation.CreateSessionAsync"/> call.</summary>
/// <param name="Branch">
/// The branch the new workspace got, which for a worktree the naming templates chose here rather
/// than in the caller — so the caller can show the session's branch without guessing at it.
/// </param>
public sealed record CreateSessionResult(Session Session, string InstanceId, string WorkspaceId, string? Branch = null);
