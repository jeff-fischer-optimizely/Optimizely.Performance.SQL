using System;
using System.Collections.Generic;
using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// What the shim has learned about one target database. Probed once per
    /// server/database pair and cached for the process lifetime.
    /// </summary>
    public sealed class DatabaseCapabilities
    {
        /// <summary>
        /// Placeholder used before a probe has run, or when probing is switched off.
        /// Nothing is known, so every precondition fails and only unconditional rewrites apply.
        /// </summary>
        public static readonly DatabaseCapabilities Unknown = new DatabaseCapabilities();

        private readonly HashSet<string> _indexes;
        private readonly Dictionary<string, string> _procedureBodyHashes;

        private DatabaseCapabilities()
        {
            // Empty rather than null: the collation-derived properties are public and
            // Unknown is what every failed probe returns, so they must answer "no" rather
            // than throw.
            Collation = string.Empty;
            _indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _procedureBodyHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IsProbed = false;
        }

        public DatabaseCapabilities(
            string collation,
            int sqlServerMajorVersion,
            int compatibilityLevel,
            IEnumerable<string> indexNames,
            IEnumerable<KeyValuePair<string, string>> procedureBodyHashes = null)
        {
            Collation = collation ?? string.Empty;
            SqlServerMajorVersion = sqlServerMajorVersion;
            CompatibilityLevel = compatibilityLevel;

            _indexes = new HashSet<string>(
                indexNames ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            _procedureBodyHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (procedureBodyHashes != null)
            {
                foreach (var entry in procedureBodyHashes)
                {
                    if (!string.IsNullOrEmpty(entry.Key))
                    {
                        _procedureBodyHashes[Normalize(entry.Key)] = entry.Value;
                    }
                }
            }

            IsProbed = true;
        }

        /// <summary>True once a probe has successfully completed.</summary>
        public bool IsProbed { get; }

        /// <summary>Database collation name, e.g. <c>SQL_Latin1_General_CP1_CI_AS</c>.</summary>
        public string Collation { get; }

        /// <summary>Product major version: 11 = SQL Server 2012, 16 = 2022.</summary>
        public int SqlServerMajorVersion { get; }

        /// <summary>
        /// Database compatibility level: 110 = SQL Server 2012 behaviour, 150 = 2019.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="SqlServerMajorVersion"/> and far more often the binding
        /// constraint. Sampling across the estate found production databases running at 110
        /// on a current Azure SQL engine, where the optimiser behaves as SQL Server 2012
        /// regardless of what the engine is capable of. Any rewrite whose plan shape depends
        /// on optimiser generation must gate on this, not on the product version.
        /// </remarks>
        public int CompatibilityLevel { get; }

        /// <summary>
        /// True when the collation is case-insensitive. This is the gate on every rewrite
        /// that removes a case-folding function from a predicate.
        /// </summary>
        public bool IsCaseInsensitive
        {
            get { return Collation.IndexOf("_CI_", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        /// <summary>True when the collation is accent-insensitive.</summary>
        public bool IsAccentInsensitive
        {
            get { return Collation.IndexOf("_AI", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        /// <summary>True when an index of this name exists in the database.</summary>
        public bool HasIndex(string indexName)
        {
            return !string.IsNullOrEmpty(indexName) && _indexes.Contains(indexName);
        }

        /// <summary>True when a procedure of this name exists in the database.</summary>
        public bool HasProcedure(string procedureName)
        {
            return !string.IsNullOrEmpty(procedureName)
                && _procedureBodyHashes.ContainsKey(Normalize(procedureName));
        }

        /// <summary>
        /// True when the procedure exists and its body hashes to <paramref name="expectedHash"/>.
        /// An unknown or absent hash returns false, so drift is treated as unsafe.
        /// </summary>
        public bool ProcedureBodyMatches(string procedureName, string expectedHash)
        {
            if (string.IsNullOrEmpty(procedureName) || string.IsNullOrEmpty(expectedHash))
            {
                return false;
            }

            string actual;
            return _procedureBodyHashes.TryGetValue(Normalize(procedureName), out actual)
                && string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reduces a procedure reference to a bare name for comparison. Call sites spell
        /// the same procedure as <c>netContentLoad</c>, <c>dbo.netContentLoad</c> and
        /// <c>[dbo].[netContentLoad]</c> interchangeably.
        /// </summary>
        internal static string Normalize(string procedureName)
        {
            if (string.IsNullOrEmpty(procedureName))
            {
                return string.Empty;
            }

            var name = procedureName.Trim();

            // Keep only the final identifier: strip database and schema qualifiers.
            var lastDot = name.LastIndexOf('.');

            if (lastDot >= 0 && lastDot < name.Length - 1)
            {
                name = name.Substring(lastDot + 1);
            }

            return name.Trim().Trim('[', ']', '"').Trim();
        }

        /// <summary>
        /// Evaluates a rewrite's preconditions. Unconditional rewrites pass even when
        /// nothing has been probed; anything with a requirement fails closed.
        /// </summary>
        public bool Satisfies(RewritePreconditions preconditions)
        {
            if (preconditions == null || preconditions.IsUnconditional)
            {
                return true;
            }

            if (!IsProbed)
            {
                return false;
            }

            if (preconditions.RequiresCaseInsensitiveCollation && !IsCaseInsensitive)
            {
                return false;
            }

            if (preconditions.RequiresAccentInsensitiveCollation && !IsAccentInsensitive)
            {
                return false;
            }

            if (preconditions.MinimumSqlServerMajorVersion.HasValue
                && SqlServerMajorVersion < preconditions.MinimumSqlServerMajorVersion.Value)
            {
                return false;
            }

            if (preconditions.MinimumCompatibilityLevel.HasValue
                && CompatibilityLevel < preconditions.MinimumCompatibilityLevel.Value)
            {
                return false;
            }

            if (preconditions.RequiredIndexes != null)
            {
                foreach (var index in preconditions.RequiredIndexes)
                {
                    if (!HasIndex(index))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
