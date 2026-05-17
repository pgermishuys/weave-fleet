namespace NuCode.Audit;

/// <summary>
/// Records tool invocations and permission decisions to a persistent audit log.
/// </summary>
internal interface IAuditService
{
    /// <summary>
    /// Records a completed (or failed) tool invocation.
    /// </summary>
    Task RecordToolInvocationAsync(AuditEntry entry);

    /// <summary>
    /// Records a permission decision.
    /// </summary>
    Task RecordPermissionDecisionAsync(AuditPermissionEntry entry);
}
