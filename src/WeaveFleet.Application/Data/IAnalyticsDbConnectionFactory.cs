using System.Data;

namespace WeaveFleet.Application.Data;

/// <summary>
/// Abstraction for obtaining analytics database connections. Separate from the main
/// <see cref="IDbConnectionFactory"/> to prevent accidental cross-wiring between the
/// main database and the analytics database.
/// </summary>
public interface IAnalyticsDbConnectionFactory
{
    IDbConnection CreateConnection();
}
