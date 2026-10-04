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
        SupportsCommands = false,
        // --fork-session copies the conversation only when the fork's first prompt runs, so it would take in whatever
        // the session did since. Fleet also keeps Claude Code's history itself, which a fork doesn't copy.
        SupportsForking = false,
        SupportsResume = true,
        SupportsImageAttachments = false,
        SupportsStreaming = true,
        SupportsDelegation = false,
        // Background shells, monitors and subagents (task_* messages); stop_task stops one, and a command's output file
        // can be read.
        ReportsBackgroundWork = true,
        // A subagent's steps go to a hidden child session, which Claude Code can't prompt on its own.
        SupportsChildSessions = true,
        ChildSessionsResumable = false,
        // Fleet's notes to the model go ahead of the prompt as text blocks of their own.
        TakesModelNotes = true,
    };
}
