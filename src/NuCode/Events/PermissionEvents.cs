using NuCode.Permissions;

namespace NuCode.Events;

/// <summary>
/// Permission-related events.
/// </summary>
public static class PermissionEvents
{
    /// <summary>Properties for permission asked events.</summary>
    public sealed record PermissionAskedInfo(
        string RequestId,
        SessionId SessionId,
        string Permission,
        IReadOnlyList<string> Patterns);

    /// <summary>A permission request was created and is awaiting user decision.</summary>
    public static readonly NuCodeEventDefinition<PermissionAskedInfo> Asked = new("permission.asked");

    /// <summary>Properties for permission replied events.</summary>
    public sealed record PermissionRepliedInfo(
        string RequestId,
        SessionId SessionId,
        PermissionDecision Decision);

    /// <summary>A user replied to a permission request.</summary>
    public static readonly NuCodeEventDefinition<PermissionRepliedInfo> Replied = new("permission.replied");
}
