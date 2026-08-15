using WeaveFleet.Application.Services;
using WeaveFleet.Domain.Events;
using WeaveFleet.Domain.Harnesses;

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// Fake implementation of <see cref="ISessionMessageProxy"/> for testing.
/// Returns empty results by default. Configure behavior via public properties.
/// </summary>
public sealed class FakeSessionMessageProxy : ISessionMessageProxy
{
    public Func<string, int, string?, CancellationToken, Task<SessionSnapshot>>? GetSnapshotBehavior { get; set; }
    public Func<string, int?, string?, CancellationToken, Task<MessagePage>>? GetMessagesBehavior { get; set; }

    public Task<SessionSnapshot> GetSnapshotAsync(
        string fleetSessionId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken ct = default)
    {
        if (GetSnapshotBehavior is not null)
            return GetSnapshotBehavior(fleetSessionId, pageSize, cursor, ct);

        // Default: return empty snapshot
        return Task.FromResult(new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = fleetSessionId,
                Title = "Test Session",
                Status = "active"
            },
            Messages = [],
            Delegations = [],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false
        });
    }

    public Task<MessagePage> GetMessagesAsync(
        string fleetSessionId,
        int? limit = null,
        string? before = null,
        CancellationToken ct = default)
    {
        if (GetMessagesBehavior is not null)
            return GetMessagesBehavior(fleetSessionId, limit, before, ct);

        // Default: return empty page
        return Task.FromResult(new MessagePage([], false));
    }
}
