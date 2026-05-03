namespace NuCode.Permissions;

/// <summary>
/// A single permission rule mapping a permission type and pattern to an action.
/// </summary>
/// <param name="Permission">The permission type (e.g., "bash", "edit", "write", "external_directory").</param>
/// <param name="Pattern">The wildcard pattern to match against (e.g., "git *", "*.cs", "*").</param>
/// <param name="Action">The action to take when this rule matches.</param>
public sealed record PermissionRule(string Permission, string Pattern, PermissionAction Action);
