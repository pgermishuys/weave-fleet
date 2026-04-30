using System.Data;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Applies analytics SQL migrations to the analytics SQLite database.
/// </summary>
public sealed class AnalyticsMigrationRunner
{
    internal const string AnalyticsJournalTable = "analytics_schema_versions";

    private readonly MigrationRunner _inner;

    public AnalyticsMigrationRunner(
        IAnalyticsDbConnectionFactory connectionFactory,
        ILogger<MigrationRunner> logger)
    {
        var adapter = new AnalyticsConnectionFactoryAdapter(connectionFactory);
        _inner = new MigrationRunner(adapter, logger, "AnalyticsMigrations", AnalyticsJournalTable);
    }

    public Task ApplyMigrationsAsync() => _inner.ApplyMigrationsAsync();

    public Task ApplyMigrationsAsync(IDbConnection connection) => _inner.ApplyMigrationsAsync(connection);

    private sealed class AnalyticsConnectionFactoryAdapter(IAnalyticsDbConnectionFactory inner) : IDbConnectionFactory
    {
        public IDbConnection CreateConnection() => inner.CreateConnection();
    }
}
