using System.Data;
using Microsoft.Extensions.Logging;
using WeaveFleet.Application.Data;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Adapter that bridges <see cref="IAnalyticsDbConnectionFactory"/> to <see cref="IDbConnectionFactory"/>
/// so it can be used with the generic <see cref="MigrationRunner"/>.
/// </summary>
internal sealed class AnalyticsConnectionFactoryAdapter : IDbConnectionFactory
{
    private readonly IAnalyticsDbConnectionFactory _inner;

    public AnalyticsConnectionFactoryAdapter(IAnalyticsDbConnectionFactory inner)
        => _inner = inner;

    public IDbConnection CreateConnection() => _inner.CreateConnection();
}

/// <summary>
/// Applies analytics SQL migrations to the analytics SQLite database.
/// Reuses <see cref="MigrationRunner"/> with the <c>"AnalyticsMigrations"</c> folder segment
/// to ensure only analytics migrations are applied — not main DB migrations.
/// </summary>
public sealed class AnalyticsMigrationRunner
{
    private readonly MigrationRunner _inner;

    public AnalyticsMigrationRunner(
        IAnalyticsDbConnectionFactory connectionFactory,
        ILogger<MigrationRunner> logger)
    {
        var adapter = new AnalyticsConnectionFactoryAdapter(connectionFactory);
        _inner = new MigrationRunner(adapter, logger, folderSegment: "AnalyticsMigrations");
    }

    public Task ApplyMigrationsAsync() => _inner.ApplyMigrationsAsync();
}
