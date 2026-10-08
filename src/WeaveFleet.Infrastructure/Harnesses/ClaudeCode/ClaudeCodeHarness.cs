using WeaveFleet.Application.Harnesses;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Infrastructure.Harnesses.ClaudeCode;

/// <summary>
/// Descriptor-only <see cref="IHarness"/> implementation for the Claude Code AI coding agent.
/// Provides static metadata: type identifier, display name, and capabilities.
/// Runtime provisioning is handled by <see cref="ClaudeCodeHarnessRuntime"/>.
/// </summary>
public sealed class ClaudeCodeHarness : IHarness
{
    /// <inheritdoc />
    public string Type => "claude-code";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public HarnessCapabilities Capabilities { get; } = new()
    {
        // Each prompt starts its own claude process, so the session can exist before the first one.
        // The first message is then saved and delivered like any other.
        RequiresInitialPrompt = false,
        SupportsAgents = false,
        SupportsModelSelection = true,
        // Its commands and skills, Fleet's built-in skills among them, sent as "/name arguments" for Claude Code to expand.
        SupportsCommands = true,
        // --fork-session copies the conversation only when the fork's first prompt runs, so it would take in whatever
        // the session did since. Fleet also keeps Claude Code's history itself, which a fork doesn't copy.
        SupportsForking = false,
        // Its own /compact, sent as a prompt.
        SupportsCompaction = true,
        SupportsResume = true,
        // Sent as image blocks ahead of the prompt's text.
        SupportsImageAttachments = true,
        SupportsStreaming = true,
        SupportsDelegation = false,
        // A message sent mid-turn goes to the claude process at once, and Claude Code reads it when the agent's current
        // step ends, in the same turn.
        SupportsSteering = true,
        SteersByDefault = true,
        // Background shells, monitors and subagents (task_* messages); stop_task stops one, and a command's output file
        // can be read.
        ReportsBackgroundWork = true,
        // A subagent's steps go to a hidden child session, which Claude Code can't prompt on its own.
        SupportsChildSessions = true,
        ChildSessionsResumable = false,
        // Fleet's notes to the model go ahead of the prompt as text blocks of their own.
        TakesModelNotes = true,
        // Fleet's MCP server gives the agent Fleet's tools (ClaudeCodeFleetTools), fleet_session_read among them.
        SupportsFleetTools = true,
        // fleet_browser_read and fleet_browser_act, as on OpenCode.
        SupportsAgentBrowser = true,
        // One process per session, so only a step's process gets fleet_step_done; Fleet refuses a subagent's call.
        SupportsWorkflowSteps = true,
        // --permission-mode for the level, and its asks come to Fleet through the stdio permission prompt tool.
        SupportsPermissionLevels = true,
    };

    /// <inheritdoc />
    public HarnessPresentation Presentation { get; } = new()
    {
        Order = 2,
        // The usage limits it reports are the Claude subscription's, not Claude Code's.
        ShortName = "Claude",
        Eyebrow = "CLI harness",
        Description = "Harness for Anthropic Claude Code sessions and project-aware coding workflows.",
        Pitch = "Anthropic's coding tool. Needs a Claude subscription or an API key.",
        Icon = HarnessIcons.Hexagon,
        PermissionModes = new(
            Ask: "--permission-mode default, asking Fleet",
            Edits: "--permission-mode acceptEdits, asking Fleet",
            All: "--permission-mode bypassPermissions"),
        AgentBrowser = "Fleet's browser tools: read the page, act on it.",
    };
}
