using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.V12.Diagnostics;

namespace Optimizely.Performance.SQL.V12.Tests
{
    /// <summary>
    /// The adapter's side of its contract with <c>Microsoft.Data.SqlClient</c>, driven by a
    /// synthetic diagnostic listener rather than a database.
    /// </summary>
    /// <remarks>
    /// The events and payload shape are copied from what SqlClient 5.2 actually emits,
    /// verified by observation. Testing against a stand-in is what makes the awkward cases
    /// reachable at all: a completion event that never arrives, a payload missing the
    /// operation id, disposal with work still in flight. None of those can be provoked
    /// through a real provider on demand.
    /// </remarks>
    public sealed class DiagnosticContractTests : IDisposable
    {
        private const string Original = "SELECT 1 FROM dbo.tblContent WHERE LOWER(Name) = LOWER(@Name)";
        private const string Rewritten = "SELECT 1 FROM dbo.tblContent WHERE Name = @Name";

        private readonly DiagnosticListener _listener;
        private readonly SqlClientDiagnosticSubscriber _subscriber;
        private readonly IDisposable _allListeners;

        public DiagnosticContractTests()
        {
            _subscriber = new SqlClientDiagnosticSubscriber(Context());

            // Subscribing through AllListeners is how the shim really attaches, so the test
            // exercises the discovery path too rather than handing the listener over.
            _allListeners = DiagnosticListener.AllListeners.Subscribe(_subscriber);
            _listener = new DiagnosticListener(SqlClientDiagnosticSubscriber.ListenerName);
        }

        [Fact]
        public void Command_is_rewritten_before_execution()
        {
            var command = new FakeCommand { CommandText = Original };

            Before(command, Guid.NewGuid());

            AssertRewritten(command);
        }

        [Fact]
        public void Command_is_restored_after_execution()
        {
            var command = new FakeCommand { CommandText = Original };
            var operationId = Guid.NewGuid();

            Before(command, operationId);
            After(command, operationId);

            Assert.Equal(Original, command.CommandText);
            Assert.Equal(0, _subscriber.InFlight);
        }

        /// <summary>
        /// SqlClient raises the error event instead of the completion event when a command
        /// throws. Missing it would leave the command rewritten for whoever retries it.
        /// </summary>
        [Fact]
        public void Command_is_restored_after_a_failed_execution()
        {
            var command = new FakeCommand { CommandText = Original };
            var operationId = Guid.NewGuid();

            Before(command, operationId);
            Write(SqlClientDiagnosticSubscriber.CommandError, command, operationId);

            Assert.Equal(Original, command.CommandText);
            Assert.Equal(0, _subscriber.InFlight);
        }

        [Fact]
        public void Unapproved_statement_is_left_alone()
        {
            var command = new FakeCommand { CommandText = "SELECT 1" };

            Before(command, Guid.NewGuid());

            Assert.Equal("SELECT 1", command.CommandText);
            Assert.Equal(0, _subscriber.Applied);
            Assert.Equal(0, _subscriber.InFlight);
        }

        /// <summary>
        /// Two concurrent executions are matched by operation id, not by arrival order.
        /// </summary>
        [Fact]
        public void Overlapping_executions_are_matched_by_operation_id()
        {
            var first = new FakeCommand { CommandText = Original };
            var second = new FakeCommand { CommandText = Original };
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();

            Before(first, firstId);
            Before(second, secondId);

            AssertRewritten(first);
            AssertRewritten(second);

            // Completed out of order, as async execution routinely does.
            After(second, secondId);

            Assert.Equal(Original, second.CommandText);
            AssertRewritten(first);

            After(first, firstId);

            Assert.Equal(Original, first.CommandText);
            Assert.Equal(0, _subscriber.InFlight);
        }

        [Fact]
        public void Completion_for_an_unknown_operation_is_ignored()
        {
            var command = new FakeCommand { CommandText = Original };

            After(command, Guid.NewGuid());

            Assert.Equal(Original, command.CommandText);
            Assert.Equal(0, _subscriber.Restored);
        }

        /// <summary>
        /// Without an operation id there is no key to match a completion event against, so
        /// the rewrite could never be undone. It is withdrawn immediately instead.
        /// </summary>
        [Fact]
        public void Payload_without_an_operation_id_does_not_rewrite()
        {
            var command = new FakeCommand { CommandText = Original };

            _listener.Write(SqlClientDiagnosticSubscriber.CommandBefore, new { Command = command });

            Assert.Equal(Original, command.CommandText);
            Assert.Equal(0, _subscriber.InFlight);
        }

        [Fact]
        public void Payload_without_a_command_is_ignored()
        {
            _listener.Write(SqlClientDiagnosticSubscriber.CommandBefore, new { OperationId = Guid.NewGuid() });

            Assert.Equal(0, _subscriber.Applied);
        }

        [Fact]
        public void Null_payload_is_ignored()
        {
            _listener.Write(SqlClientDiagnosticSubscriber.CommandBefore, null);

            Assert.Equal(0, _subscriber.Applied);
        }

        /// <summary>
        /// Uninstalling with work outstanding must not strand a rewritten command.
        /// </summary>
        [Fact]
        public void Disposing_restores_anything_still_in_flight()
        {
            var command = new FakeCommand { CommandText = Original };

            Before(command, Guid.NewGuid());
            AssertRewritten(command);

            _subscriber.Dispose();

            Assert.Equal(Original, command.CommandText);
            Assert.Equal(0, _subscriber.InFlight);
        }

        [Fact]
        public void Connection_events_are_not_subscribed_to()
        {
            // The subscription filters to the three command events, so SqlClient never has
            // to build payloads for anything else.
            Assert.False(_listener.IsEnabled("Microsoft.Data.SqlClient.WriteConnectionOpenBefore"));
            Assert.True(_listener.IsEnabled(SqlClientDiagnosticSubscriber.CommandBefore));
        }

        [Fact]
        public void Applied_and_restored_stay_balanced()
        {
            for (var i = 0; i < 5; i++)
            {
                var command = new FakeCommand { CommandText = Original };
                var operationId = Guid.NewGuid();

                Before(command, operationId);
                After(command, operationId);
            }

            Assert.Equal(5, _subscriber.Applied);
            Assert.Equal(5, _subscriber.Restored);
        }

        /// <summary>
        /// Asserts the command is carrying the approved replacement, annotated.
        /// </summary>
        /// <remarks>
        /// The annotation is not incidental. It is what makes a rewrite identifiable in a
        /// profiler trace or an execution-plan cache, which is how anyone confirms from the
        /// server side that the shim is doing what it claims.
        /// </remarks>
        private static void AssertRewritten(DbCommand command)
        {
            Assert.Contains("opti-perf-sql:V12-0001", command.CommandText);
            Assert.Contains(Rewritten, command.CommandText);
        }

        // ---- plumbing --------------------------------------------------------------

        private void Before(DbCommand command, Guid operationId)
        {
            Write(SqlClientDiagnosticSubscriber.CommandBefore, command, operationId);
        }

        private void After(DbCommand command, Guid operationId)
        {
            Write(SqlClientDiagnosticSubscriber.CommandAfter, command, operationId);
        }

        /// <summary>
        /// Writes a payload shaped like SqlClient's: an anonymous type carrying the command
        /// and the operation id, read back by reflection.
        /// </summary>
        private void Write(string eventName, DbCommand command, Guid operationId)
        {
            _listener.Write(
                eventName,
                new
                {
                    OperationId = operationId,
                    Operation = "ExecuteReader",
                    ConnectionId = (Guid?)Guid.Empty,
                    Command = command,
                    Timestamp = 0L
                });
        }

        private static RewriteContext Context()
        {
            var options = new RewriteOptions { ProbeDatabaseCapabilities = false };

            var document = new ApprovedSqlDocument
            {
                Statements = new[]
                {
                    new ApprovedStatement
                    {
                        Id = "V12-0001",
                        Kind = RewriteKind.StatementRewrite,
                        OriginalSql = Original,
                        RewrittenSql = Rewritten,
                        AppliesTo = CmsVersion.All,
                        Enabled = true
                    }
                }
            };

            return new RewriteContext(new SqlRewriteRegistry(document, options), options);
        }

        public void Dispose()
        {
            _subscriber.Dispose();
            _allListeners.Dispose();
            _listener.Dispose();
        }

        /// <summary>
        /// The least a <see cref="DbCommand"/> can be and still carry text and parameters.
        /// </summary>
        /// <remarks>
        /// A fake is right here: the adapter reaches the command as a <see cref="DbCommand"/>
        /// precisely so it does not care which provider produced it, and these tests are
        /// about the event contract, not about SQL Server.
        /// </remarks>
        private sealed class FakeCommand : DbCommand
        {
            private readonly FakeParameterCollection _parameters = new FakeParameterCollection();

            public override string CommandText { get; set; }
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; } = CommandType.Text;
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection DbConnection { get; set; }
            protected override DbTransaction DbTransaction { get; set; }

            protected override DbParameterCollection DbParameterCollection
            {
                get { return _parameters; }
            }

            public override void Cancel() { }
            public override int ExecuteNonQuery() => 0;
            public override object ExecuteScalar() => null;
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => null;
        }

        private sealed class FakeParameter : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; }
            public override bool IsNullable { get; set; }
            public override string ParameterName { get; set; }
            public override string SourceColumn { get; set; }
            public override bool SourceColumnNullMapping { get; set; }
            public override int Size { get; set; }
            public override object Value { get; set; }
            public override void ResetDbType() { }
        }

        private sealed class FakeParameterCollection : DbParameterCollection
        {
            private readonly List<DbParameter> _items = new List<DbParameter>();

            public override int Count => _items.Count;
            public override object SyncRoot => _items;

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

            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => IndexOf(value) >= 0;
            public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
            public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
            protected override void SetParameter(string parameterName, DbParameter value) => _items[IndexOf(parameterName)] = value;

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
        }
    }
}
