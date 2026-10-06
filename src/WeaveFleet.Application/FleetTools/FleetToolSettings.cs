using WeaveFleet.Application.Browser;
using WeaveFleet.Application.Memory;
using WeaveFleet.Application.Services;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Application.Workflows;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.FleetTools;

/// <summary>
/// Reads which of Fleet's switchable tools a session gets right now (<see cref="FleetToolSwitches"/>), from its owner's
/// settings and whether the session is a workflow step that the agent finishes. Fleet's MCP server lists the tools from
/// it, and a harness that serves them over MCP reads it when it starts a process, to start another when it changes.
/// </summary>
public sealed class FleetToolSettings(
    IBackgroundUserScope userScope,
    ISessionRepository sessions,
    IUserPreferenceRepository preferences,
    SessionMessagesFeature sessionMessages,
    AgentMemoryFeature memory,
    WorkflowsFeature workflows)
{
    public async Task<FleetToolSwitches> ForSessionAsync(string userId, string fleetSessionId)
    {
        using (userScope.Begin(userId))
        {
            // Only a step the agent finishes has the step tool: the user ends one they finish, with Move on.
            var session = await sessions.GetByIdAsync(fleetSessionId).ConfigureAwait(false);
            var workflowStep = session is { WorkflowRunId: not null, WorkflowUserFinishes: false }
                               && await workflows.IsEnabledAsync().ConfigureAwait(false);
            return new FleetToolSwitches(
                SessionMessages: await sessionMessages.IsEnabledAsync().ConfigureAwait(false),
                Memory: await memory.IsEnabledAsync().ConfigureAwait(false),
                WorkflowStep: workflowStep,
                Browser: AgentBrowserSettings.From(await preferences.GetAllAsync().ConfigureAwait(false)).Enabled);
        }
    }
}
