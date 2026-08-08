using WeaveFleet.Application.Data;
using WeaveFleet.Domain.Repositories;

namespace WeaveFleet.Infrastructure.Data.Repositories;

public sealed class AutomationEventLedgerRepository : IAutomationEventLedgerRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public AutomationEventLedgerRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<bool> IsProcessedAsync(string automationId, string sourceEventId)
    {
        using var conn = _connectionFactory.CreateConnection();
        var result = await conn.ExecuteScalarAsync<long?>(
            """
            SELECT 1
            FROM automation_event_ledger
            WHERE automation_id = @AutomationId AND source_event_id = @SourceEventId
            LIMIT 1
            """,
            cmd =>
            {
                cmd.AddParameter("AutomationId", automationId);
                cmd.AddParameter("SourceEventId", sourceEventId);
            });

        return result.HasValue;
    }

    public async Task RecordAsync(string automationId, string sourceEventId)
    {
        using var conn = _connectionFactory.CreateConnection();
        await conn.ExecuteNonQueryAsync(
            """
            INSERT OR IGNORE INTO automation_event_ledger (automation_id, source_event_id, processed_at)
            VALUES (@AutomationId, @SourceEventId, @ProcessedAt)
            """,
            cmd =>
            {
                cmd.AddParameter("AutomationId", automationId);
                cmd.AddParameter("SourceEventId", sourceEventId);
                cmd.AddParameter("ProcessedAt", DateTime.UtcNow.ToString("O"));
            });
    }
}
