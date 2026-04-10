using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// User context for background services and startup recovery code that run without an <c>HttpContext</c>.
/// Examples: <see cref="HarnessEventRelay"/>, analytics services, <c>MarkAllStoppedAsync</c>.
/// </summary>
public sealed class SystemUserContext : IUserContext
{
    public string UserId => "system";
    public string? Email => null;
    public string? DisplayName => "System";
    public bool IsAuthenticated => false;
}
