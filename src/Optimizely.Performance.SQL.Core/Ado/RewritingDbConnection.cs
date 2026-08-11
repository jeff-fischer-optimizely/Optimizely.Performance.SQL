using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Ado
{
    /// <summary>
    /// Pass-through connection whose only job is to hand out
    /// <see cref="RewritingDbCommand"/> instances.
    /// </summary>
    public class RewritingDbConnection : DbConnection
    {
        private readonly DbConnection _inner;
        private readonly RewriteContext _context;
        private readonly DbProviderFactory _factory;

        public RewritingDbConnection(DbConnection inner, RewriteContext context, DbProviderFactory factory = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _factory = factory;

            // Forward provider events so consumers relying on them keep working.
            _inner.StateChange += (sender, args) => OnStateChange(args);
        }

        /// <summary>The provider connection being decorated.</summary>
        public DbConnection InnerConnection
        {
            get { return _inner; }
        }

        public override string ConnectionString
        {
            get { return _inner.ConnectionString; }
            set { _inner.ConnectionString = value; }
        }

        public override int ConnectionTimeout
        {
            get { return _inner.ConnectionTimeout; }
        }

        public override string Database
        {
            get { return _inner.Database; }
        }

        public override string DataSource
        {
            get { return _inner.DataSource; }
        }

        public override string ServerVersion
        {
            get { return _inner.ServerVersion; }
        }

        public override ConnectionState State
        {
            get { return _inner.State; }
        }

        protected override DbProviderFactory DbProviderFactory
        {
            get { return _factory; }
        }

        public override void ChangeDatabase(string databaseName)
        {
            _inner.ChangeDatabase(databaseName);
        }

        public override void Open()
        {
            _inner.Open();
        }

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            return _inner.OpenAsync(cancellationToken);
        }

        public override void Close()
        {
            _inner.Close();
        }

        public override DataTable GetSchema()
        {
            return _inner.GetSchema();
        }

        public override DataTable GetSchema(string collectionName)
        {
            return _inner.GetSchema(collectionName);
        }

        public override DataTable GetSchema(string collectionName, string[] restrictionValues)
        {
            return _inner.GetSchema(collectionName, restrictionValues);
        }

        public override void EnlistTransaction(System.Transactions.Transaction transaction)
        {
            _inner.EnlistTransaction(transaction);
        }

        protected override DbCommand CreateDbCommand()
        {
            var command = new RewritingDbCommand(_inner.CreateCommand(), _context);

            // Route the command back through this wrapper so callers reading
            // command.Connection see the object they handed us.
            command.Connection = this;

            return command;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            return new RewritingDbTransaction(_inner.BeginTransaction(isolationLevel), this);
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
