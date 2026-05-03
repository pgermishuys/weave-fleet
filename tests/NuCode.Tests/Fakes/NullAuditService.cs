using NuCode.Audit;

namespace NuCode.Fakes;

/// <summary>
/// A no-op <see cref="IAuditService"/> for use in tests.
/// </summary>
internal sealed class NullAuditService : IAuditService
{
    public Task RecordToolInvocationAsync(AuditEntry entry) => Task.CompletedTask;
    public Task RecordPermissionDecisionAsync(AuditPermissionEntry entry) => Task.CompletedTask;
}
