using WeaveFleet.Application.Canvases;
using WeaveFleet.Application.Services;

namespace WeaveFleet.Application.Tests.Canvases;

/// <summary>The request's own user, until a scope says otherwise, like the real user contexts.</summary>
internal sealed class ScopedUser : IUserContext, IBackgroundUserScope
{
    public const string RequestUser = "request-user";

    private readonly AsyncLocal<string?> _scoped = new();

    public string UserId => _scoped.Value ?? RequestUser;
    public string? Email => null;
    public string? DisplayName => UserId;
    public bool IsAuthenticated => true;

    public IDisposable Begin(string userId)
    {
        var previous = _scoped.Value;
        _scoped.Value = userId;
        return new Restore(() => _scoped.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

internal sealed class FakeCallers : IHarnessCanvasCallerResolver
{
    private readonly Dictionary<(string Token, string SessionId), HarnessCanvasCaller> _callers = [];

    public void Add(string token, string openCodeSessionId, HarnessCanvasCaller caller) => _callers[(token, openCodeSessionId)] = caller;

    public Task<HarnessCanvasCaller?> ResolveAsync(string bridgeToken, string harnessSessionId, CancellationToken ct = default)
        => Task.FromResult(_callers.GetValueOrDefault((bridgeToken, harnessSessionId)));
}
