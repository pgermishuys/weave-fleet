using System.Text;
using Dapper;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Expands list parameters into individual numbered parameters for SQLite compatibility
/// with Dapper.AOT, which does not support Dapper's runtime IN @List expansion.
/// </summary>
internal static class SqlInExpander
{
    /// <summary>
    /// Appends an IN clause with expanded parameters to the SQL builder and adds them to <paramref name="parameters"/>.
    /// For example, given prefix "Status" and values ["active", "stopped"], appends "IN (@Status0, @Status1)"
    /// and adds parameters @Status0 = "active", @Status1 = "stopped".
    /// </summary>
    public static void AppendInClause<T>(
        StringBuilder sql,
        DynamicParameters parameters,
        string prefix,
        IReadOnlyList<T> values)
    {
        sql.Append("IN (");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                sql.Append(", ");
            var paramName = $"@{prefix}{i}";
            sql.Append(paramName);
            parameters.Add($"{prefix}{i}", values[i]);
        }
        sql.Append(')');
    }
}
