using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Ado
{
    /// <summary>
    /// The interception point. Wraps a provider command and swaps its text for the
    /// approved replacement immediately before execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The substitution is scoped to the execution: the inner command's text and
    /// parameters are restored afterwards. Callers therefore observe the command exactly
    /// as they configured it, which matters because EPiServer reuses command objects and
    /// reads <c>CommandText</c> back for its own logging.
    /// </para>
    /// <para>
    /// Both command types are handled, differently. For <see cref="CommandType.Text"/>
    /// the statement is fingerprinted and its text replaced. For
    /// <see cref="CommandType.StoredProcedure"/> the text is a procedure name, so the
    /// name is swapped for a versioned copy carrying the optimised body; the shipped
    /// procedure is never altered and the call itself is unchanged.
    /// </para>
    /// </remarks>
    public class RewritingDbCommand : DbCommand
    {
        private readonly DbCommand _inner;
        private readonly RewriteContext _context;
        private DbConnection _connection;
        private DbTransaction _transaction;

        public RewritingDbCommand(DbCommand inner, RewriteContext context)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>The provider command being decorated.</summary>
        public DbCommand InnerCommand
        {
            get { return _inner; }
        }

        public override string CommandText
        {
            get { return _inner.CommandText; }
            set { _inner.CommandText = value; }
        }

        public override int CommandTimeout
        {
            get { return _inner.CommandTimeout; }
            set { _inner.CommandTimeout = value; }
        }

        public override CommandType CommandType
        {
            get { return _inner.CommandType; }
            set { _inner.CommandType = value; }
        }

        public override bool DesignTimeVisible
        {
            get { return _inner.DesignTimeVisible; }
            set { _inner.DesignTimeVisible = value; }
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get { return _inner.UpdatedRowSource; }
            set { _inner.UpdatedRowSource = value; }
        }

        protected override DbConnection DbConnection
        {
            get { return _connection; }
            set
            {
                _connection = value;

                // Unwrap: the provider command must be given the provider connection.
                var wrapper = value as RewritingDbConnection;
                _inner.Connection = wrapper != null ? wrapper.InnerConnection : value;
            }
        }

        protected override DbParameterCollection DbParameterCollection
        {
            get { return _inner.Parameters; }
        }

        protected override DbTransaction DbTransaction
        {
            get { return _transaction; }
            set
            {
                _transaction = value;

                var wrapper = value as RewritingDbTransaction;
                _inner.Transaction = wrapper != null ? wrapper.InnerTransaction : value;
            }
        }

        public override void Cancel()
        {
            _inner.Cancel();
        }

        public override void Prepare()
        {
            _inner.Prepare();
        }

        protected override DbParameter CreateDbParameter()
        {
            return _inner.CreateParameter();
        }

        public override int ExecuteNonQuery()
        {
            using (var scope = BeginRewrite())
            {
                return _inner.ExecuteNonQuery();
            }
        }

        public override object ExecuteScalar()
        {
            using (var scope = BeginRewrite())
            {
                return _inner.ExecuteScalar();
            }
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            using (var scope = BeginRewrite())
            {
                return _inner.ExecuteReader(behavior);
            }
        }

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            using (var scope = BeginRewrite())
            {
                return await _inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override async Task<object> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            using (var scope = BeginRewrite())
            {
                return await _inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken)
        {
            using (var scope = BeginRewrite())
            {
                return await _inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolves the statement and applies the substitution, returning a scope that
        /// undoes it. A failure anywhere in here leaves the command untouched and the
        /// original statement runs, which is the only acceptable failure mode for a shim
        /// sitting in front of every query in the CMS.
        /// </summary>
        private RewriteScope BeginRewrite()
        {
            var commandType = _inner.CommandType;

            if (_context.IsInert
                || (commandType != CommandType.Text && commandType != CommandType.StoredProcedure))
            {
                return RewriteScope.None;
            }

            // Procedure redirects are rare and are configured per deployment. Skip the
            // probe entirely when none are loaded, so a sproc-heavy stack such as CMS 11
            // pays nothing for the feature being present.
            if (commandType == CommandType.StoredProcedure && !_context.Registry.HasProcedureRedirects)
            {
                return RewriteScope.None;
            }

            try
            {
                // The transaction goes with the connection: a probe issued outside the
                // caller's pending transaction is rejected by the provider.
                var capabilities = _context.Options.ProbeDatabaseCapabilities
                    ? _context.CapabilityProvider.GetCapabilities(_inner.Connection, _inner.Transaction)
                    : DatabaseCapabilities.Unknown;

                var result = commandType == CommandType.StoredProcedure
                    ? _context.Registry.ResolveProcedure(_inner.CommandText, capabilities)
                    : _context.Registry.Resolve(_inner.CommandText, _inner.Parameters, capabilities);

                if (!result.ShouldReplace)
                {
                    return RewriteScope.None;
                }

                return RewriteScope.Apply(_inner, result);
            }
            catch
            {
                // Fail open: an unrewritten query is slow, a failed query is an outage.
                return RewriteScope.None;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Applies a substitution for the duration of one execution and reverses it on
        /// dispose, including any parameters the variant made redundant.
        /// </summary>
        private readonly struct RewriteScope : IDisposable
        {
            public static readonly RewriteScope None = default;

            private readonly DbCommand _command;
            private readonly string _originalText;
            private readonly List<KeyValuePair<int, DbParameter>> _removed;

            private RewriteScope(
                DbCommand command,
                string originalText,
                List<KeyValuePair<int, DbParameter>> removed)
            {
                _command = command;
                _originalText = originalText;
                _removed = removed;
            }

            public static RewriteScope Apply(DbCommand command, RewriteResult result)
            {
                var originalText = command.CommandText;
                var removed = RemoveDroppedParameters(command, result.DroppedParameters);

                command.CommandText = result.Sql;

                return new RewriteScope(command, originalText, removed);
            }

            /// <summary>
            /// Strips parameters the replacement no longer references, keeping the
            /// <c>sp_executesql</c> signature aligned with the batch actually being run.
            /// </summary>
            private static List<KeyValuePair<int, DbParameter>> RemoveDroppedParameters(
                DbCommand command,
                string[] dropped)
            {
                if (dropped == null || dropped.Length == 0 || command.Parameters.Count == 0)
                {
                    return null;
                }

                List<KeyValuePair<int, DbParameter>> removed = null;

                foreach (var name in dropped)
                {
                    for (var i = command.Parameters.Count - 1; i >= 0; i--)
                    {
                        var parameter = command.Parameters[i];

                        if (parameter == null || !NameMatches(parameter.ParameterName, name))
                        {
                            continue;
                        }

                        removed ??= new List<KeyValuePair<int, DbParameter>>();
                        removed.Add(new KeyValuePair<int, DbParameter>(i, parameter));
                        command.Parameters.RemoveAt(i);
                    }
                }

                return removed;
            }

            private static bool NameMatches(string actual, string expected)
            {
                return string.Equals(Strip(actual), Strip(expected), StringComparison.OrdinalIgnoreCase);
            }

            private static string Strip(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return string.Empty;
                }

                var first = name[0];
                return first == '@' || first == ':' || first == '?' ? name.Substring(1) : name;
            }

            public void Dispose()
            {
                if (_command == null)
                {
                    return;
                }

                _command.CommandText = _originalText;

                if (_removed == null)
                {
                    return;
                }

                // Re-insert ascending by original index so every parameter lands back
                // where it started.
                for (var i = _removed.Count - 1; i >= 0; i--)
                {
                    var entry = _removed[i];
                    var index = Math.Min(entry.Key, _command.Parameters.Count);
                    _command.Parameters.Insert(index, entry.Value);
                }
            }
        }
    }
}
