using System;
using System.Data;
using System.Data.Common;

namespace Optimizely.Performance.SQL.Ado
{
    /// <summary>
    /// Pass-through transaction. It exists purely so that
    /// <see cref="DbTransaction.Connection"/> returns the wrapper rather than the
    /// provider connection; code that reads it back and creates further commands would
    /// otherwise escape the shim.
    /// </summary>
    public class RewritingDbTransaction : DbTransaction
    {
        private readonly DbTransaction _inner;
        private readonly RewritingDbConnection _connection;

        public RewritingDbTransaction(DbTransaction inner, RewritingDbConnection connection)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _connection = connection;
        }

        /// <summary>The provider transaction being decorated.</summary>
        public DbTransaction InnerTransaction
        {
            get { return _inner; }
        }

        public override IsolationLevel IsolationLevel
        {
            get { return _inner.IsolationLevel; }
        }

        protected override DbConnection DbConnection
        {
            get { return _connection; }
        }

        public override void Commit()
        {
            _inner.Commit();
        }

        public override void Rollback()
        {
            _inner.Rollback();
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
