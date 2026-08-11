using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Probes collation, product version and index presence once per database and caches
    /// the answer for the process lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The probe runs on the caller's connection, which means it happens inside somebody's
    /// request. It is one round trip per database, once, and it is worth it: without it
    /// the shim cannot prove that dropping a case-folding function preserves semantics,
    /// so every collation-sensitive rewrite would have to be skipped.
    /// </para>
    /// <para>
    /// A failed probe is cached as a failure. Retrying on every command against a
    /// database that cannot answer would turn one bad connection into sustained load.
    /// </para>
    /// </remarks>
    public sealed class SqlServerCapabilityProvider : IDatabaseCapabilityProvider
    {
        private const string ProbeSql = @"
SELECT
    CONVERT(nvarchar(256), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));
SELECT name FROM sys.indexes WHERE name IS NOT NULL;";

        private readonly ConcurrentDictionary<string, DatabaseCapabilities> _cache =
            new ConcurrentDictionary<string, DatabaseCapabilities>(StringComparer.OrdinalIgnoreCase);

        private readonly int _timeoutSeconds;

        public SqlServerCapabilityProvider(int timeoutSeconds = 5)
        {
            _timeoutSeconds = timeoutSeconds;
        }

        public DatabaseCapabilities GetCapabilities(DbConnection connection)
        {
            if (connection == null)
            {
                return DatabaseCapabilities.Unknown;
            }

            var key = BuildKey(connection);

            if (key.Length == 0)
            {
                return DatabaseCapabilities.Unknown;
            }

            DatabaseCapabilities cached;
            if (_cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            // Only probe on a connection somebody else already opened. Opening one here
            // could deadlock inside a provider callback or exhaust the pool.
            if (connection.State != ConnectionState.Open)
            {
                return DatabaseCapabilities.Unknown;
            }

            var probed = Probe(connection);

            // Cache failures too, so a database that cannot answer is asked only once.
            _cache.TryAdd(key, probed);

            return probed;
        }

        private DatabaseCapabilities Probe(DbConnection connection)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ProbeSql;
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = _timeoutSeconds;

                    var collation = string.Empty;
                    var majorVersion = 0;
                    var indexes = new List<string>();

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            collation = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            majorVersion = ParseMajorVersion(reader.IsDBNull(1) ? null : reader.GetString(1));
                        }

                        if (reader.NextResult())
                        {
                            while (reader.Read())
                            {
                                if (!reader.IsDBNull(0))
                                {
                                    indexes.Add(reader.GetString(0));
                                }
                            }
                        }
                    }

                    return new DatabaseCapabilities(collation, majorVersion, indexes);
                }
            }
            catch
            {
                // An unprobeable database simply gets no conditional rewrites.
                return DatabaseCapabilities.Unknown;
            }
        }

        private static int ParseMajorVersion(string productVersion)
        {
            if (string.IsNullOrEmpty(productVersion))
            {
                return 0;
            }

            var dot = productVersion.IndexOf('.');
            var head = dot > 0 ? productVersion.Substring(0, dot) : productVersion;

            int value;
            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        /// <summary>
        /// Cache key: server plus database. Deliberately not the full connection string,
        /// which carries credentials and pooling knobs that do not change the answer.
        /// </summary>
        private static string BuildKey(DbConnection connection)
        {
            try
            {
                return (connection.DataSource ?? string.Empty) + "/" + (connection.Database ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
