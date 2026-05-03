namespace NuCode.Permissions;

/// <summary>
/// Represents the action to take for a permission check.
/// </summary>
public enum PermissionAction
{
    /// <summary>Allow the operation.</summary>
    Allow,
    /// <summary>Deny the operation.</summary>
    Deny,
    /// <summary>Ask the user for approval.</summary>
    Ask,
}
