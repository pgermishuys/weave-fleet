namespace WeaveFleet.Application.Updates;

/// <summary>
/// Set once this Fleet has agreed to restart into a staged update. From then on it takes no new turns (a turn started
/// now would be cut off a moment later), and a node that was asked to restart tells the rollout it is on its way.
/// </summary>
public sealed class UpdateRestartGate
{
    private int _restarting;

    public bool IsRestarting => Volatile.Read(ref _restarting) == 1;

    /// <summary>True for the caller that closed the gate; false when it was closed already.</summary>
    public bool TryClose() => Interlocked.CompareExchange(ref _restarting, 1, 0) == 0;

    /// <summary>Opens the gate again, when the restart turned out to be unnecessary (a turn started first).</summary>
    public void Reopen() => Volatile.Write(ref _restarting, 0);
}
