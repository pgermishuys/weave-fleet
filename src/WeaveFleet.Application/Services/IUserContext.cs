namespace WeaveFleet.Application.Services;

/// <summary>
/// Provides identity information for the current request or execution context.
/// Scoped: one instance per request (HTTP or hosted-service scope).
/// </summary>
public interface IUserContext
{
    /// <summary>Stable user identifier — matches the IdP "sub" claim in cloud mode, "local-user" in local mode.</summary>
    string UserId { get; }

    /// <summary>User's email address, or null when unavailable.</summary>
    string? Email { get; }

    /// <summary>User's display name, or null when unavailable.</summary>
    string? DisplayName { get; }

    /// <summary>True when a real identity has been established; false for system/background contexts.</summary>
    bool IsAuthenticated { get; }
}
