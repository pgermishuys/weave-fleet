using System.Data;
using WeaveFleet.Application.Data;

#pragma warning disable CS8766 // Nullability of reference types in return type
#pragma warning disable CS8767 // Nullability of reference types in type of parameter

namespace WeaveFleet.Testing.Fakes;

/// <summary>
/// A fake <see cref="IDbConnectionFactory"/> that returns a <see cref="FakeDbConnection"/>.
/// Replaces IDbConnection/IDbTransaction mocks in tests that don't need real SQL.
/// </summary>
public sealed class FakeDbConnectionFactory : IDbConnectionFactory
{
    public IDbConnection CreateConnection() => new FakeDbConnection();
}

/// <summary>
/// A no-op <see cref="IDbConnection"/> implementation for tests.
/// </summary>
public sealed class FakeDbConnection : IDbConnection
{
    public string ConnectionString { get; set; } = string.Empty;
    public int ConnectionTimeout => 30;
    public string Database => "fake";
    public ConnectionState State => ConnectionState.Open;

    public IDbTransaction BeginTransaction() => new FakeDbTransaction(this);
    public IDbTransaction BeginTransaction(IsolationLevel il) => new FakeDbTransaction(this);

    public void ChangeDatabase(string databaseName) { }
    public void Close() { }
    public void Open() { }

    public IDbCommand CreateCommand() => new FakeDbCommand();

    public void Dispose() { }
}

/// <summary>
/// A no-op <see cref="IDbTransaction"/> implementation for tests.
/// </summary>
public sealed class FakeDbTransaction : IDbTransaction
{
    public FakeDbTransaction(IDbConnection connection) => Connection = connection;

    public IDbConnection? Connection { get; }
    public IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    public void Commit() { }
    public void Rollback() { }
    public void Dispose() { }
}

/// <summary>
/// A no-op <see cref="IDbCommand"/> implementation for tests.
/// </summary>
internal sealed class FakeDbCommand : IDbCommand
{
    public string CommandText { get; set; } = string.Empty;
    public int CommandTimeout { get; set; } = 30;
    public CommandType CommandType { get; set; } = CommandType.Text;
    public IDbConnection? Connection { get; set; }
    public IDataParameterCollection Parameters { get; } = new FakeParameterCollection();
    public IDbTransaction? Transaction { get; set; }
    public UpdateRowSource UpdatedRowSource { get; set; }

    public void Cancel() { }
    public IDbDataParameter CreateParameter() => new FakeDbDataParameter();
    public void Dispose() { }
    public int ExecuteNonQuery() => 0;
    public IDataReader ExecuteReader() => throw new NotSupportedException("FakeDbCommand does not support ExecuteReader.");
    public IDataReader ExecuteReader(CommandBehavior behavior) => throw new NotSupportedException("FakeDbCommand does not support ExecuteReader.");
    public object? ExecuteScalar() => null;
    public void Prepare() { }
}

internal sealed class FakeParameterCollection : List<object>, IDataParameterCollection
{
    public bool Contains(string parameterName) => false;
    public int IndexOf(string parameterName) => -1;
    public void RemoveAt(string parameterName) { }
    public object this[string parameterName] { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
}

internal sealed class FakeDbDataParameter : IDbDataParameter
{
    public DbType DbType { get; set; }
    public ParameterDirection Direction { get; set; }
    public bool IsNullable => false;
    public string ParameterName { get; set; } = string.Empty;
    public string SourceColumn { get; set; } = string.Empty;
    public DataRowVersion SourceVersion { get; set; }
    public object? Value { get; set; }
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public int Size { get; set; }
}
