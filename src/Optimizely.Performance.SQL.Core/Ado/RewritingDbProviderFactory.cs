using System;
using System.Data.Common;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Ado
{
    /// <summary>
    /// Decorating provider factory. Everything it creates other than connections and
    /// commands is delegated untouched to the real SqlClient factory.
    /// </summary>
    /// <remarks>
    /// This is the seam the whole shim hangs from. Whatever creates connections in the
    /// CMS (<c>DbProviderFactories</c> on CMS 11, dependency injection on CMS 12) is
    /// pointed at this type instead of <c>SqlClientFactory</c>, and every command created
    /// downstream is intercepted without a single call site changing.
    /// </remarks>
    public class RewritingDbProviderFactory : DbProviderFactory
    {
        private readonly DbProviderFactory _inner;
        private readonly RewriteContext _context;

        public RewritingDbProviderFactory(DbProviderFactory inner, RewriteContext context)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>The provider factory being decorated.</summary>
        public DbProviderFactory InnerFactory
        {
            get { return _inner; }
        }

        /// <summary>The shared rewrite state handed to every command this factory produces.</summary>
        public RewriteContext Context
        {
            get { return _context; }
        }

        public override DbConnection CreateConnection()
        {
            return new RewritingDbConnection(_inner.CreateConnection(), _context, this);
        }

        public override DbCommand CreateCommand()
        {
            return new RewritingDbCommand(_inner.CreateCommand(), _context);
        }

        public override DbParameter CreateParameter()
        {
            return _inner.CreateParameter();
        }

        public override DbConnectionStringBuilder CreateConnectionStringBuilder()
        {
            return _inner.CreateConnectionStringBuilder();
        }

        public override DbCommandBuilder CreateCommandBuilder()
        {
            return _inner.CreateCommandBuilder();
        }

        public override DbDataAdapter CreateDataAdapter()
        {
            return _inner.CreateDataAdapter();
        }

        public override bool CanCreateDataSourceEnumerator
        {
            get { return _inner.CanCreateDataSourceEnumerator; }
        }

        public override DbDataSourceEnumerator CreateDataSourceEnumerator()
        {
            return _inner.CreateDataSourceEnumerator();
        }
    }
}
