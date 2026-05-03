namespace NuCode.Agents;

/// <summary>
/// Defines the operational mode of an agent.
/// </summary>
public enum AgentMode
{
    /// <summary>
    /// A primary agent that the user interacts with directly.
    /// </summary>
    Primary,

    /// <summary>
    /// A subagent that is invoked by other agents via the task tool.
    /// </summary>
    SubAgent,

    /// <summary>
    /// An agent that can operate in both primary and subagent mode.
    /// </summary>
    All,
}
