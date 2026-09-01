using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Optimizely.Performance.SQL.Fingerprinting;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Probes collation, product version, compatibility level, index presence and
    /// procedure bodies once per database and caches the answer for the process lifetime.
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
        // Compatibility level is read alongside the product version because the two
        // routinely disagree: sampling the estate found production databases at level 110
        // on a 12.x Azure SQL engine. The product version alone would let a rewrite that
        // needs a modern optimiser through onto a 2012-era one.
        private const string ProbeSql = @"
SELECT
    CONVERT(nvarchar(256), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
    CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel'));

SELECT name FROM sys.indexes WHERE name IS NOT NULL;";

        private readonly ConcurrentDictionary<string, DatabaseCapabilities> _cache =
            new ConcurrentDictionary<string, DatabaseCapabilities>(StringComparer.OrdinalIgnoreCase);

        private readonly string[] _proceduresOfInterest;
        private readonly int _timeoutSeconds;

        /// <param name="proceduresOfInterest">
        /// Procedures whose bodies should be read and hashed: the originals and
        /// replacements named by the loaded procedure redirects. Pass null or empty when
        /// there are none, and no procedure metadata is fetched at all.
        /// </param>
        /// <param name="timeoutSeconds">Probe command timeout.</param>
        /// <remarks>
        /// The set is passed in rather than discovered because an EPiServer database holds
        /// several hundred procedures and their bodies run to megabytes. Fetching only the
        /// handful under redirect keeps the probe to one small round trip.
        /// </remarks>
        public SqlServerCapabilityProvider(
            IEnumerable<string> proceduresOfInterest = null,
            int timeoutSeconds = 5)
        {
            _timeoutSeconds = timeoutSeconds;
            _proceduresOfInterest = Distinct(proceduresOfInterest);
        }

        private static string[] Distinct(IEnumerable<string> names)
        {
            if (names == null)
            {
                return Array.Empty<string>();
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                var normalized = DatabaseCapabilities.Normalize(name);

                if (normalized.Length != 0)
                {
                    seen.Add(normalized);
                }
            }

            var result = new string[seen.Count];
            seen.CopyTo(result);
            return result;
        }

        public DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null)
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

            var probed = Probe(connection, transaction);

            // Cache failures too, so a database that cannot answer is asked only once.
            _cache.TryAdd(key, probed);

            return probed;
        }

        private DatabaseCapabilities Probe(DbConnection connection, DbTransaction transaction)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ProbeSql;
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = _timeoutSeconds;

                    // Enlist in whatever the intercepted command is already inside.
                    // SqlClient rejects an unenlisted command on a connection holding a
                    // pending local transaction, and since failures are cached, probing
                    // for the first time inside a transaction would otherwise disable
                    // every conditional rewrite on this database for the process lifetime.
                    command.Transaction = transaction;

                    var collation = string.Empty;
                    var majorVersion = 0;
                    var compatibilityLevel = 0;
                    var indexes = new List<string>();

                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            collation = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                            majorVersion = ParseMajorVersion(reader.IsDBNull(1) ? null : reader.GetString(1));
                            compatibilityLevel = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
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

                    // Separate round trip, separately guarded. If this fails the database
                    // still gets its collation- and version-gated rewrites; only the
                    // procedure redirects stand down.
                    var procedures = ProbeProcedures(connection, transaction);

                    return new DatabaseCapabilities(
                        collation,
                        majorVersion,
                        compatibilityLevel,
                        indexes,
                        procedures);
                }
            }
            catch
            {
                // An unprobeable database simply gets no conditional rewrites.
                return DatabaseCapabilities.Unknown;
            }
        }

        /// <summary>
        /// Reads the bodies of the procedures under redirect and hashes them client-side.
        /// Returns an empty set when nothing is under redirect or the read fails.
        /// </summary>
        private List<KeyValuePair<string, string>> ProbeProcedures(DbConnection connection, DbTransaction transaction)
        {
            var result = new List<KeyValuePair<string, string>>();

            if (_proceduresOfInterest.Length == 0)
            {
                return result;
            }

            try
            {
                using (var command = connection.CreateCommand())
                {
                    var sql = new StringBuilder(
                        "SELECT o.name, m.definition FROM sys.sql_modules AS m" +
                        " JOIN sys.objects AS o ON o.object_id = m.object_id" +
                        " WHERE o.type = 'P' AND o.name IN (");

                    for (var i = 0; i < _proceduresOfInterest.Length; i++)
                    {
                        if (i > 0)
                        {
                            sql.Append(',');
                        }

                        var name = "@p" + i.ToString(CultureInfo.InvariantCulture);
                        sql.Append(name);

                        // Parameterised rather than interpolated. The names come from our
                        // own configuration, but a probe is no place to build SQL by
                        // concatenation.
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = name;
                        parameter.DbType = DbType.String;
                        parameter.Size = 128;
                        parameter.Value = _proceduresOfInterest[i];
                        command.Parameters.Add(parameter);
                    }

                    sql.Append(')');

                    command.CommandText = sql.ToString();
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = _timeoutSeconds;
                    command.Transaction = transaction;

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0))
                            {
                                continue;
                            }

                            var name = reader.GetString(0);
                            var definition = reader.IsDBNull(1) ? null : reader.GetString(1);

                            result.Add(new KeyValuePair<string, string>(
                                name,
                                ModuleHash.Compute(definition)));
                        }
                    }
                }
            }
            catch
            {
                // Encrypted or unreadable modules leave the redirect inactive, which is
                // the safe direction.
                result.Clear();
            }

            return result;
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
