using WeaveFleet.Application.Events;
using WeaveFleet.Domain.Events;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeSessionSnapshotBuilder : ISessionSnapshotBuilder
{
    // ── Call tracking ────────────────────────────────────────────────────────

    public List<(string SessionId, int PageSize, string? Cursor)> BuildAsyncCalls { get; } = [];

    // ── Configurable behaviors ───────────────────────────────────────────────

    /// <summary>
    /// Optional override for <see cref="BuildAsync"/>. When set, called instead of returning an empty snapshot.
    /// </summary>
    public Func<string, int, string?, Task<SessionSnapshot>>? BuildBehavior { get; set; }

    // ── ISessionSnapshotBuilder ──────────────────────────────────────────────

    public Task<SessionSnapshot> BuildAsync(string sessionId, int pageSize = 100, string? cursor = null)
    {
        BuildAsyncCalls.Add((sessionId, pageSize, cursor));

        if (BuildBehavior is not null)
            return BuildBehavior(sessionId, pageSize, cursor);

        // Default: return an empty snapshot
        return Task.FromResult(new SessionSnapshot
        {
            Session = new SessionSnapshotSession
            {
                Id = sessionId,
                Title = "Test Session",
                Status = "active",
            },
            Messages = [],
            Delegations = [],
            ActivityStatus = "idle",
            HasMore = false,
            IsPartial = false,
        });
    }
}
