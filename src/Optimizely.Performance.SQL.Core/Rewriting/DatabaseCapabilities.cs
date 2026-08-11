using System;
using System.Collections.Generic;
using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// What the shim has learned about one target database. Probed once per connection
    /// string and cached for the process lifetime.
    /// </summary>
    public sealed class DatabaseCapabilities
    {
        /// <summary>
        /// Placeholder used before a probe has run, or when probing is switched off.
        /// Nothing is known, so every precondition fails and only unconditional rewrites apply.
        /// </summary>
        public static readonly DatabaseCapabilities Unknown = new DatabaseCapabilities();

        private readonly HashSet<string> _indexes;

        private DatabaseCapabilities()
        {
            _indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IsProbed = false;
        }

        public DatabaseCapabilities(
            string collation,
            int sqlServerMajorVersion,
            IEnumerable<string> indexNames)
        {
            Collation = collation ?? string.Empty;
            SqlServerMajorVersion = sqlServerMajorVersion;
            _indexes = new HashSet<string>(
                indexNames ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            IsProbed = true;
        }

        /// <summary>True once a probe has successfully completed.</summary>
        public bool IsProbed { get; }

        /// <summary>Database collation name, e.g. <c>SQL_Latin1_General_CP1_CI_AS</c>.</summary>
        public string Collation { get; }

        /// <summary>Product major version: 11 = SQL Server 2012, 16 = 2022.</summary>
        public int SqlServerMajorVersion { get; }

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
