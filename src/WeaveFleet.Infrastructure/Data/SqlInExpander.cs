using System.Data.Common;
using System.Text;

namespace WeaveFleet.Infrastructure.Data;

/// <summary>
/// Expands list parameters into individual numbered parameters for SQLite compatibility.
/// </summary>
internal static class SqlInExpander
{
    /// <summary>
    /// Appends an IN clause with expanded parameters to the SQL builder and adds them to <paramref name="cmd"/>.
    /// For example, given prefix "Status" and values ["active", "stopped"], appends "IN (@Status0, @Status1)"
    /// and adds parameters @Status0 = "active", @Status1 = "stopped".
    /// </summary>
    public static void AppendInClause<T>(
        StringBuilder sql,
        DbCommand cmd,
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
            cmd.AddParameter($"{prefix}{i}", values[i]);
        }
        sql.Append(')');
    }
}
