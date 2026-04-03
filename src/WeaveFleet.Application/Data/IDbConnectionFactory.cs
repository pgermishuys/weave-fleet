using System.Data;

namespace WeaveFleet.Application.Data;

/// <summary>
/// Abstraction for obtaining database connections. Services depend on this interface,
/// not on concrete SQLite types.
/// </summary>
public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}
