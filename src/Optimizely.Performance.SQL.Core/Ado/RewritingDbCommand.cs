using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Optimizely.Performance.SQL.Interception;
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
        /// undoes it.
        /// </summary>
        /// <remarks>
        /// The decision itself lives in <see cref="CommandRewriter"/> so this decorator
        /// and the CMS 11 / CMS 12 adapters cannot disagree about what is safe to
        /// substitute.
        /// </remarks>
        private CommandRewrite BeginRewrite()
        {
            return CommandRewriter.Apply(_inner, _context);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
