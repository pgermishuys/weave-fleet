using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Sessions;

/// <summary>
/// Interface for activating sessions on-demand.
/// Breaks circular dependency between SessionActivation and OpenCodeSessionMessageProxy.
/// </summary>
public interface ISessionActivator
{
    /// <summary>
    /// Activates a session by its ID, returning the live harness instance.
    /// If the session is already active, returns the existing instance.
    /// </summary>
    Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Wakes a session only to read from its harness: a session whose checkout Clean now removed stays asleep with its
    /// checkout removed, unless its harness keeps the conversation (OpenCode), which can't be read asleep.
    /// </summary>
    Task<Result<IHarnessSession>> ActivateToReadAsync(string sessionId, CancellationToken ct = default)
        => ActivateSessionAsync(sessionId, ct);
}
