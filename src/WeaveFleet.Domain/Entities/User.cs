namespace WeaveFleet.Domain.Entities;

/// <summary>
/// Shadow user record created on first authenticated login.
/// The <see cref="Id"/> matches the IdP "sub" claim.
/// </summary>
public sealed class User
{
    /// <summary>Stable identifier matching the IdP "sub" claim.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User's email address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>User's display name, or null when unavailable.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Account status: "active" (default) or "disabled".</summary>
    public string Status { get; set; } = "active";

    /// <summary>ISO 8601 creation timestamp.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>ISO 8601 timestamp of the most recent successful login, or null.</summary>
    public string? LastLoginAt { get; set; }

    /// <summary>ISO 8601 timestamp when onboarding was completed, or null.</summary>
    public string? OnboardingCompletedAt { get; set; }
}
