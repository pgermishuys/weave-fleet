using WeaveFleet.Application.Services;

namespace WeaveFleet.Infrastructure.Services;

/// <summary>
/// User context for local (non-authenticated) mode.
/// Returns hardcoded "local-user" identity. Registered when <c>Auth.Enabled = false</c>.
/// </summary>
public sealed class LocalUserContext : IUserContext
{
    public string UserId => BackgroundUserContext.UserId ?? "local-user";
    public string? Email => null;
    public string? DisplayName => BackgroundUserContext.UserId ?? "Local User";
    public bool IsAuthenticated => true;
}
