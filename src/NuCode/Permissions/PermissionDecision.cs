namespace NuCode.Permissions;

/// <summary>
/// Represents a user's decision when asked for permission.
/// </summary>
public enum PermissionDecision
{
    /// <summary>Allow this specific request only.</summary>
    Allow,
    /// <summary>Allow this request and remember for future matching requests.</summary>
    AlwaysAllow,
    /// <summary>Deny this request.</summary>
    Deny,
}
