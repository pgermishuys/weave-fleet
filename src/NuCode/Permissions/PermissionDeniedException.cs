namespace NuCode.Permissions;

/// <summary>
/// Thrown when a permission check results in denial.
/// </summary>
public sealed class PermissionDeniedException : Exception
{
    public PermissionDeniedException(string permission, string pattern)
        : base($"Permission denied: {permission} for pattern '{pattern}'")
    {
        Permission = permission;
        Pattern = pattern;
    }

    /// <summary>Gets the permission type that was denied.</summary>
    public string Permission { get; }

    /// <summary>Gets the pattern that was denied.</summary>
    public string Pattern { get; }
}
