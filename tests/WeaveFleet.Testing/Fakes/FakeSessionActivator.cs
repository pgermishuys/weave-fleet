using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Common;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// Fake implementation of ISessionActivator for testing.
/// </summary>
public sealed class FakeSessionActivator : ISessionActivator
{
    public Func<string, CancellationToken, Task<Result<IHarnessSession>>>? ActivateBehavior { get; set; }

    public List<(string SessionId, CancellationToken Ct)> ActivateAsyncCalls { get; } = [];

    public Task<Result<IHarnessSession>> ActivateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ActivateAsyncCalls.Add((sessionId, ct));

        if (ActivateBehavior is not null)
            return ActivateBehavior(sessionId, ct);

        // Default: return failure
        return Task.FromResult(Result.Failure<IHarnessSession>(FleetError.NotFoundFor("Instance", sessionId)));
    }
}
