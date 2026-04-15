using WeaveFleet.Application.Analytics;

namespace WeaveFleet.Testing.Fakes;

public sealed class FakeAnalyticsCollector : IAnalyticsCollector
{
    public List<TokenEventData> TokenEvents { get; } = [];
    public List<SessionSnapshotData> SessionSnapshots { get; } = [];

    public void AcceptTokenEvent(TokenEventData data) => TokenEvents.Add(data);

    public void AcceptSessionSnapshot(SessionSnapshotData data) => SessionSnapshots.Add(data);
}
