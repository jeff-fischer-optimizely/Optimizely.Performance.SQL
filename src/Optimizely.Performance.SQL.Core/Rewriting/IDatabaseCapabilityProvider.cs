using System.Data.Common;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Supplies the probed facts a rewrite's preconditions are checked against.
    /// </summary>
    public interface IDatabaseCapabilityProvider
    {
        /// <summary>
        /// Returns what is known about the database behind <paramref name="connection"/>.
        /// Implementations must be cheap on the hot path: probe once, cache by connection
        /// string, and return <see cref="DatabaseCapabilities.Unknown"/> rather than
        /// throwing or blocking if the answer is not yet available.
        /// </summary>
        /// <param name="connection">The connection the intercepted command will run on.</param>
        /// <param name="transaction">
        /// The transaction that command is enlisted in, or null. It must be passed on to
        /// any command the probe issues: SqlClient refuses to execute an unenlisted
        /// command on a connection with a pending local transaction, and the resulting
        /// failure would be cached as "nothing is known about this database".
        /// </param>
        DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null);
    }

    /// <summary>
    /// Provider used when probing is switched off. Reports nothing, which means only
    /// unconditional rewrites are ever applied.
    /// </summary>
    public sealed class NullCapabilityProvider : IDatabaseCapabilityProvider
    {
        public static readonly NullCapabilityProvider Instance = new NullCapabilityProvider();

        private NullCapabilityProvider()
        {
        }

        public DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null)
        {
            return DatabaseCapabilities.Unknown;
        }
    }
}
