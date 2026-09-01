using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace Optimizely.Performance.SQL.Tests.Fakes
{
    /// <summary>
    /// One command execution, captured at the moment the inner provider saw it.
    /// </summary>
    /// <remarks>
    /// The decorator restores <c>CommandText</c> and any dropped parameters once
    /// execution returns, so reading them afterwards tells you nothing. The only way to
    /// observe what was actually sent is to record it from inside the provider, which is
    /// what this exists for.
    /// </remarks>
    public sealed class ExecutedCommand
    {
        public ExecutedCommand(string commandText, CommandType commandType, IEnumerable<string> parameterNames)
        {
            CommandText = commandText;
            CommandType = commandType;
            ParameterNames = new List<string>(parameterNames);
        }

        public string CommandText { get; }

        public CommandType CommandType { get; }

        public IReadOnlyList<string> ParameterNames { get; }
    }

    /// <summary>Shared execution log, so a test can inspect every statement the provider saw.</summary>
    public sealed class ExecutionLog
    {
        private readonly List<ExecutedCommand> _entries = new List<ExecutedCommand>();

        public IReadOnlyList<ExecutedCommand> Entries
        {
            get { return _entries; }
        }

        public ExecutedCommand Last
        {
            get { return _entries.Count == 0 ? null : _entries[_entries.Count - 1]; }
        }

        public void Record(ExecutedCommand entry)
        {
            _entries.Add(entry);
        }
    }

    public sealed class FakeDbProviderFactory : DbProviderFactory
    {
        public FakeDbProviderFactory(ExecutionLog log)
        {
            Log = log;
        }

        public ExecutionLog Log { get; }

        public override DbConnection CreateConnection()
        {
            return new FakeDbConnection(Log);
        }

        public override DbCommand CreateCommand()
        {
            return new FakeDbCommand(Log);
        }

        public override DbParameter CreateParameter()
        {
            return new FakeDbParameter();
        }
    }

    public sealed class FakeDbConnection : DbConnection
    {
        private readonly ExecutionLog _log;
        private ConnectionState _state = ConnectionState.Closed;
        private string _connectionString = string.Empty;

        public FakeDbConnection(ExecutionLog log)
        {
            _log = log;
        }

        public override string ConnectionString
        {
            get { return _connectionString; }
            set { _connectionString = value; }
        }

        public override string Database
        {
            get { return "FakeDb"; }
        }

        public override string DataSource
        {
            get { return "fake"; }
        }

        public override string ServerVersion
        {
            get { return "0.0"; }
        }

        public override ConnectionState State
        {
            get { return _state; }
        }

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Open()
        {
            Transition(ConnectionState.Open);
        }

        public override void Close()
        {
            Transition(ConnectionState.Closed);
        }

        private void Transition(ConnectionState to)
        {
            var from = _state;
            _state = to;

            if (from != to)
            {
                OnStateChange(new StateChangeEventArgs(from, to));
            }
        }

        protected override DbCommand CreateDbCommand()
        {
            return new FakeDbCommand(_log) { Connection = this };
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return new FakeDbTransaction(this, isolationLevel);
        }
    }

    public sealed class FakeDbTransaction : DbTransaction
    {
        private readonly DbConnection _connection;

        public FakeDbTransaction(DbConnection connection, IsolationLevel isolationLevel)
        {
            _connection = connection;
            IsolationLevel = isolationLevel;
        }

        public override IsolationLevel IsolationLevel { get; }

        protected override DbConnection DbConnection
        {
            get { return _connection; }
        }

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public override void Commit()
        {
            Committed = true;
        }

        public override void Rollback()
        {
            RolledBack = true;
        }
    }

    public sealed class FakeDbCommand : DbCommand
    {
        private readonly ExecutionLog _log;
        private readonly FakeDbParameterCollection _parameters = new FakeDbParameterCollection();

        public FakeDbCommand(ExecutionLog log)
        {
            _log = log;
            CommandText = string.Empty;
            CommandType = CommandType.Text;
        }

        public override string CommandText { get; set; }

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection
        {
            get { return _parameters; }
        }

        protected override DbTransaction DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter()
        {
            return new FakeDbParameter();
        }

        public override int ExecuteNonQuery()
        {
            Capture();
            return 1;
        }

        public override object ExecuteScalar()
        {
            Capture();
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Capture();

            var table = new DataTable();
            table.Columns.Add("value", typeof(int));
            table.Rows.Add(1);

            return new DataTableReader(table);
        }

        private void Capture()
        {
            var names = new List<string>();

            for (var i = 0; i < _parameters.Count; i++)
            {
                names.Add(_parameters[i].ParameterName);
            }

            _log?.Record(new ExecutedCommand(CommandText, CommandType, names));
        }
    }

    public sealed class FakeDbParameter : DbParameter
    {
        public override DbType DbType { get; set; }

        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

        public override bool IsNullable { get; set; }

        public override string ParameterName { get; set; }

        public override int Size { get; set; }

        public override string SourceColumn { get; set; }

        public override bool SourceColumnNullMapping { get; set; }

        public override object Value { get; set; }

        public override void ResetDbType()
        {
            DbType = DbType.Object;
        }
    }

    public sealed class FakeDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _items = new List<DbParameter>();

        public override int Count
        {
            get { return _items.Count; }
        }

        public override object SyncRoot
        {
            get { return _items; }
        }

        public override int Add(object value)
        {
            _items.Add((DbParameter)value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
            {
                Add(value);
            }
        }

        public override void Clear()
        {
            _items.Clear();
        }

        public override bool Contains(object value)
        {
            return _items.Contains((DbParameter)value);
        }

        public override bool Contains(string value)
        {
            return IndexOf(value) >= 0;
        }

        public override void CopyTo(Array array, int index)
        {
            ((ICollection)_items).CopyTo(array, index);
        }

        public override IEnumerator GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        public override int IndexOf(object value)
        {
            return _items.IndexOf((DbParameter)value);
        }

        public override int IndexOf(string parameterName)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i].ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        public override void Insert(int index, object value)
        {
            _items.Insert(index, (DbParameter)value);
        }

        public override void Remove(object value)
        {
            _items.Remove((DbParameter)value);
        }

        public override void RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);

            if (index >= 0)
            {
                _items.RemoveAt(index);
            }
        }

        protected override DbParameter GetParameter(int index)
        {
            return _items[index];
        }

        protected override DbParameter GetParameter(string parameterName)
        {
            var index = IndexOf(parameterName);
            return index < 0 ? null : _items[index];
        }

        protected override void SetParameter(int index, DbParameter value)
        {
            _items[index] = value;
        }

        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);

            if (index >= 0)
            {
                _items[index] = value;
            }
        }
    }
}
