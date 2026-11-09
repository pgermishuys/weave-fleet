using WeaveFleet.Application.Configuration;
using WeaveFleet.Application.Sessions;
using WeaveFleet.Domain.Entities;

namespace WeaveFleet.Application.Machines;

/// <summary>
/// <c>@</c> references to sessions on other machines in this Fleet's list (<see cref="IRemoteSessionReferences"/>). A
/// machine can be named when an agent here could read a session there with <c>fleet_session_read</c>: it's in the list,
/// not set to Never, and this Fleet holds its token. The session is looked up there, so a message never names one
/// that isn't.
/// </summary>
public sealed class RemoteSessionReferences(RemoteMachineService machines, RemoteSessions remote, MachineIdentityStore identity)
    : IRemoteSessionReferences
{
    public string LocalMachineId => identity.Get().Id;

    public async Task<RemoteSessionReference> FindAsync(string machineId, string sessionId, bool withRecap, CancellationToken ct = default)
    {
        var here = identity.Get().Name ?? Environment.MachineName;
        var machine = (await machines.ListAsync().ConfigureAwait(false)).FirstOrDefault(listed => listed.Id == machineId);
        if (machine is null)
            return RemoteSessionReference.Failed($"is a session on a machine {here} doesn't list, so its agent couldn't read it. Add that machine in Settings › Machines on {here}.");
        if (machine.AgentWork == MachineAgentWork.Never)
            return RemoteSessionReference.Failed($"is a session on {machine.Name}, which agents on {here} can't use (it's set to Never in Settings › Machines on {here}).");

        var token = machines.TryTokenOf(machine);
        if (token is null)
            return RemoteSessionReference.Failed($"is a session on {machine.Name}, and Fleet can't read its token for {machine.Name}. Enter it again in Settings › Machines on {here}.");

        var (title, missing, error) = await remote.SessionTitleAsync(machine, token, sessionId, ct).ConfigureAwait(false);
        if (title is null)
        {
            return RemoteSessionReference.Failed(missing
                ? $"names a session {machine.Name} doesn't have. Pick it again from the @ list."
                : $"is a session on {machine.Name}, and Fleet on {here} couldn't check it there: {error}");
        }

        var recap = withRecap ? await remote.RecapAsync(machine, token, sessionId, ct).ConfigureAwait(false) : null;
        return new RemoteSessionReference(machine.Name, title, recap, null);
    }
}
