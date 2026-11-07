using WeaveFleet.Domain.Entities;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// Agents handing work to other machines: an agent in a session here starts a session on a machine in this Fleet's
/// list, gives it the task, and messages and reads it there. This Fleet makes every call with the token it keeps for
/// that machine, so the agent never sees a token. Each machine says whether agents can send it work
/// (<see cref="RemoteMachine.AgentWork"/>): ask, always or never.
/// </summary>
public static class AgentHandoff
{
    /// <summary>Set to <c>1</c> in a harness process's environment when it was started with the hand-off tools.</summary>
    public const string EnvironmentVariable = "FLEET_AGENT_HANDOFF";
}

/// <summary>
/// Whether sessions get the hand-off tools: when at least one machine in the list takes work from agents (isn't
/// <see cref="MachineAgentWork.Never"/>). Without one they'd only cost tokens. Whether a call may go ahead is decided when
/// it's made, by that machine's setting.
/// </summary>
public sealed class AgentHandoffFeature(IRemoteMachineRepository machines)
{
    public async Task<bool> IsEnabledAsync()
        => (await machines.ListAsync().ConfigureAwait(false)).Any(machine => machine.AgentWork != MachineAgentWork.Never);
}
