using System.Data;
using System.Data.Common;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Raw ADO.NET helpers that replace Dapper for NativeAOT compatibility.
/// Dapper uses Reflection.Emit at runtime which is unavailable under NativeAOT;
/// these helpers use only statically-known types and ordinal-based reader access.
/// </summary>
internal static class DbCommandHelper
{
    // ── Parameter helpers ──────────────────────────────────────────────────────

    /// <summary>Adds a named parameter to the command, mapping <c>null</c> to <see cref="DBNull.Value"/>.</summary>
    internal static void AddParameter(this DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    // ── QueryAsync ─────────────────────────────────────────────────────────────

    /// <summary>Executes a query and maps all rows using <paramref name="map"/>.</summary>
    internal static async Task<List<T>> QueryAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> map)
    {
        await using var cmd = CreateCommand(conn, sql, configure, null);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync())
            list.Add(map(reader));
        return list;
    }

    /// <summary>Executes a query with a transaction and maps all rows using <paramref name="map"/>.</summary>
    internal static async Task<List<T>> QueryAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> map,
        IDbTransaction? tx)
    {
        await using var cmd = CreateCommand(conn, sql, configure, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync())
            list.Add(map(reader));
        return list;
    }

    /// <summary>Executes a parameter-less query and maps all rows.</summary>
    internal static async Task<List<T>> QueryAsync<T>(
        this IDbConnection conn,
        string sql,
        Func<DbDataReader, T> map)
    {
        await using var cmd = CreateCommand(conn, sql, null, null);
        await using var reader = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync())
            list.Add(map(reader));
        return list;
    }

    // ── QueryFirstOrDefaultAsync ────────────────────────────────────────────────

    /// <summary>Returns the first row mapped by <paramref name="map"/>, or <c>null</c> if no rows.</summary>
    internal static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> map)
        where T : class
    {
        await using var cmd = CreateCommand(conn, sql, configure, null);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
    }

    /// <summary>Returns the first row (with transaction) mapped by <paramref name="map"/>, or <c>null</c> if no rows.</summary>
    internal static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> map,
        IDbTransaction? tx)
        where T : class
    {
        await using var cmd = CreateCommand(conn, sql, configure, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
    }

    // ── QueryFirstAsync ─────────────────────────────────────────────────────────

    /// <summary>Returns the first row mapped by <paramref name="map"/>; throws <see cref="InvalidOperationException"/> if no rows.</summary>
    internal static async Task<T> QueryFirstAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        Func<DbDataReader, T> map)
    {
        await using var cmd = CreateCommand(conn, sql, configure, null);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            return map(reader);
        throw new InvalidOperationException("Query returned no rows.");
    }

    // ── ExecuteNonQueryAsync ───────────────────────────────────────────────────

    /// <summary>Executes a non-query command and returns the number of affected rows.</summary>
    internal static async Task<int> ExecuteNonQueryAsync(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure)
    {
        await using var cmd = CreateCommand(conn, sql, configure, null);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a non-query command with a transaction and returns the number of affected rows.</summary>
    internal static async Task<int> ExecuteNonQueryAsync(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        IDbTransaction? tx)
    {
        await using var cmd = CreateCommand(conn, sql, configure, tx);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Executes a parameter-less non-query command and returns the number of affected rows.</summary>
    internal static async Task<int> ExecuteNonQueryAsync(
        this IDbConnection conn,
        string sql)
    {
        await using var cmd = CreateCommand(conn, sql, null, null);
        return await cmd.ExecuteNonQueryAsync();
    }

    // ── ExecuteScalarAsync ─────────────────────────────────────────────────────

    /// <summary>Executes a scalar command and returns the first column of the first row.</summary>
    internal static async Task<T?> ExecuteScalarAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure)
    {
        await using var cmd = CreateCommand(conn, sql, configure, null);
        var result = await cmd.ExecuteScalarAsync();
        return ConvertScalar<T>(result);
    }

    /// <summary>Executes a scalar command with a transaction and returns the first column of the first row.</summary>
    internal static async Task<T?> ExecuteScalarAsync<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure,
        IDbTransaction? tx)
    {
        await using var cmd = CreateCommand(conn, sql, configure, tx);
        var result = await cmd.ExecuteScalarAsync();
        return ConvertScalar<T>(result);
    }

    // ── DbCommand-level helpers (for dynamic SQL with pre-built commands) ──────

    /// <summary>Executes a pre-built command as a scalar and returns the converted result.</summary>
    internal static async Task<T?> ExecuteScalarAsync<T>(this DbCommand cmd)
    {
        var result = await cmd.ExecuteScalarAsync();
        return ConvertScalar<T>(result);
    }

    // ── Synchronous variants (for InProcessEventStore which must be sync) ──────

    /// <summary>Synchronously executes a scalar command and returns the first column of the first row.</summary>
    internal static T? ExecuteScalar<T>(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure)
    {
        using var cmd = CreateCommand(conn, sql, configure, null);
        var result = cmd.ExecuteScalar();
        return ConvertScalar<T>(result);
    }

    /// <summary>Synchronously executes a non-query command.</summary>
    internal static void ExecuteNonQuery(
        this IDbConnection conn,
        string sql,
        Action<DbCommand> configure)
    {
        using var cmd = CreateCommand(conn, sql, configure, null);
        cmd.ExecuteNonQuery();
    }

    // ── Reader helpers ─────────────────────────────────────────────────────────

    /// <summary>Returns the string value at <paramref name="ordinal"/>, or <c>null</c> if the column is NULL.</summary>
    internal static string? GetNullableString(this DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Returns the int32 value at <paramref name="ordinal"/>, or <c>null</c> if the column is NULL.</summary>
    internal static int? GetNullableInt32(this DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>Returns the double value at <paramref name="ordinal"/>, or <c>null</c> if the column is NULL.</summary>
    internal static double? GetNullableDouble(this DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    // ── Private helpers ────────────────────────────────────────────────────────

    private static DbCommand CreateCommand(
        IDbConnection conn,
        string sql,
        Action<DbCommand>? configure,
        IDbTransaction? tx)
    {
        var dbConn = (DbConnection)conn;
        var cmd = dbConn.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null)
            cmd.Transaction = (DbTransaction)tx;
        configure?.Invoke(cmd);
        return cmd;
    }

    private static T? ConvertScalar<T>(object? result)
    {
        if (result is null or DBNull)
            return default;
        if (result is T direct)
            return direct;
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(result, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }
}
