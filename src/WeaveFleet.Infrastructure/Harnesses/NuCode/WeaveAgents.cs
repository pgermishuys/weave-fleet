using NuCode.Agents;

namespace WeaveFleet.Infrastructure.Harnesses.NuCode;

/// <summary>
/// Weave framework agent profiles registered into NuCode sessions by weave-fleet.
/// These mirror the orchestration agents available in OpenCode-based Weave sessions.
/// </summary>
internal static class WeaveAgents
{
    /// <summary>
    /// Registers Weave orchestration agents into the given agent profile registry.
    /// Agents that already exist (e.g. "build", "explore", "general") are overridden
    /// with Weave-specific descriptions.
    /// </summary>
    public static void Register(IAgentProfileRegistry registry)
    {
        foreach (var profile in GetAll())
        {
            if (!registry.TryOverride(profile.Name, _ => profile))
            {
                registry.Register(profile);
            }
        }
    }

    private static IEnumerable<AgentProfile> GetAll()
    {
        yield return new AgentProfile
        {
            Name = "Loom (Main Orchestrator)",
            Description = "Loom (Main Orchestrator)",
            Mode = AgentMode.Primary,
        };

        yield return new AgentProfile
        {
            Name = "Tapestry (Execution Orchestrator)",
            Description = "Tapestry (Execution Orchestrator)",
            Mode = AgentMode.Primary,
        };

        yield return new AgentProfile
        {
            Name = "pattern",
            Description = "Pattern (Strategic Planner)",
            Mode = AgentMode.SubAgent,
            DeniedTools = ["todoread", "todowrite", "question"],
        };

        yield return new AgentProfile
        {
            Name = "shuttle",
            Description = "Shuttle (Domain Specialist)",
            Mode = AgentMode.SubAgent,
            DeniedTools = ["todoread", "todowrite", "question"],
        };

        yield return new AgentProfile
        {
            Name = "spindle",
            Description = "Spindle (External Researcher)",
            Mode = AgentMode.SubAgent,
            AllowedTools = ["grep", "glob", "read", "bash", "webfetch", "websearch"],
        };

        yield return new AgentProfile
        {
            Name = "thread",
            Description = "Thread (Codebase Explorer)",
            Mode = AgentMode.SubAgent,
            AllowedTools = ["grep", "glob", "read", "bash", "webfetch"],
        };

        yield return new AgentProfile
        {
            Name = "warp",
            Description = "Warp (Security Auditor)",
            Mode = AgentMode.SubAgent,
            AllowedTools = ["grep", "glob", "read", "bash"],
        };

        yield return new AgentProfile
        {
            Name = "weft",
            Description = "Weft (Reviewer/Auditor)",
            Mode = AgentMode.SubAgent,
            AllowedTools = ["grep", "glob", "read", "bash"],
        };

        yield return new AgentProfile
        {
            Name = "ralph-wiggum",
            Description = "Senior software engineer. Designs and implements features.",
            Mode = AgentMode.SubAgent,
            DeniedTools = ["todoread", "todowrite", "question"],
        };
    }
}
