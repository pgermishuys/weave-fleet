using System.Collections.Immutable;

namespace NuCode.Agents;

/// <summary>
/// Defines the configuration profile for an agent, including its behavioral parameters,
/// tool access, and permission ruleset.
/// </summary>
public sealed record AgentProfile
{
    /// <summary>
    /// Gets the unique name of this agent profile (e.g., "build", "plan", "explore").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the human-readable description of this agent.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the operational mode of this agent.
    /// </summary>
    public AgentMode Mode { get; init; } = AgentMode.Primary;

    /// <summary>
    /// Gets the system prompt override for this agent.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets the temperature parameter for LLM generation (0.0 - 2.0).
    /// When null, the provider default is used.
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Gets the top-p (nucleus sampling) parameter for LLM generation.
    /// When null, the provider default is used.
    /// </summary>
    public double? TopP { get; init; }

    /// <summary>
    /// Gets the maximum number of agentic loop steps before the agent is stopped.
    /// When null, no step limit is enforced.
    /// </summary>
    public int? MaxSteps { get; init; }

    /// <summary>
    /// Gets the set of tool names this agent is allowed to use.
    /// When null, all registered tools are available (subject to permissions).
    /// </summary>
    public ImmutableHashSet<string>? AllowedTools { get; init; }

    /// <summary>
    /// Gets the set of tool names this agent is explicitly denied from using.
    /// Applied after <see cref="AllowedTools"/>.
    /// </summary>
    public ImmutableHashSet<string>? DeniedTools { get; init; }

    /// <summary>
    /// Gets the name of the permission ruleset to apply to this agent.
    /// </summary>
    public string? PermissionRulesetName { get; init; }

    /// <summary>
    /// Gets whether this agent is hidden from the user-facing agent list.
    /// Hidden agents are typically utility agents (compaction, title generation, etc.).
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Gets whether this is a built-in (native) agent profile.
    /// </summary>
    public bool IsNative { get; init; }

    /// <summary>
    /// Gets the model identifier override for this agent (e.g., "gpt-4o").
    /// When null, the default model is used.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Gets the provider identifier override for this agent (e.g., "openai").
    /// When null, the default provider is used.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Gets the display color for this agent (used by UIs).
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Gets additional options for this agent.
    /// </summary>
    public ImmutableDictionary<string, object> Options { get; init; } =
        ImmutableDictionary<string, object>.Empty;
}
