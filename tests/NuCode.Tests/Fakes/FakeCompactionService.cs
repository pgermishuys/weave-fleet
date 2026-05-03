using NuCode.Sessions;

namespace NuCode.Fakes;

internal sealed class FakeCompactionService : ICompactionService
{
    private bool _needsCompaction;
    private int _compactCallCount;

    public int CompactCallCount => _compactCallCount;
    public SessionId? LastSessionId { get; private set; }
    public bool? LastOverflow { get; private set; }

    public void SetNeedsCompaction(bool value) => _needsCompaction = value;

    public Task<bool> NeedsCompactionAsync(SessionId sessionId, CancellationToken ct)
    {
        return Task.FromResult(_needsCompaction);
    }

    public Task CompactAsync(SessionId sessionId, bool overflow, CancellationToken ct)
    {
        _compactCallCount++;
        LastSessionId = sessionId;
        LastOverflow = overflow;
        return Task.CompletedTask;
    }
}
