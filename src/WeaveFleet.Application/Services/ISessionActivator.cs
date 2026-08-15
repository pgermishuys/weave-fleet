using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Application.Services;

/// <summary>
/// Interface for activating sessions on-demand.
/// Breaks circular dependency between SessionOrchestrator and OpenCodeSessionMessageProxy.
/// </summary>
public interface ISessionActivator
{
    /// <summary>
    /// Activates a session by its ID, returning the live harness instance.
    /// If the session is already active, returns the existing instance.
    /// </summary>
    Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default);
}
