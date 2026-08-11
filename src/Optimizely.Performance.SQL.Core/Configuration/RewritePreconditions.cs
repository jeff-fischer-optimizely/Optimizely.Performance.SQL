using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Server-side facts that must hold before a rewrite may be applied.
    /// </summary>
    /// <remarks>
    /// The motivating case is collation. Dropping <c>LOWER(col) = LOWER(@p)</c> in favour
    /// of a plain <c>col = @p</c> turns a scan into a seek, but it is only
    /// semantically equivalent when the column collation is case-insensitive. Encoding
    /// that as a precondition means the rewrite is verified against the live database
    /// once per connection string and silently skipped where it would change results.
    /// </remarks>
    public sealed class RewritePreconditions
    {
        /// <summary>
        /// When true, the rewrite is applied only if the target database collation is
        /// case-insensitive (<c>_CI_</c>). Set this on any rewrite that removes a
        /// case-folding function from a predicate.
        /// </summary>
        [JsonPropertyName("requiresCaseInsensitiveCollation")]
        public bool RequiresCaseInsensitiveCollation { get; set; }

        /// <summary>
        /// When true, the rewrite is applied only if the database collation is
        /// accent-insensitive (<c>_AI</c>).
        /// </summary>
        [JsonPropertyName("requiresAccentInsensitiveCollation")]
        public bool RequiresAccentInsensitiveCollation { get; set; }

        /// <summary>
        /// Minimum SQL Server major version (compatibility-independent product version).
        /// <c>OFFSET/FETCH</c> pagination requires 11 (SQL Server 2012); leave null when
        /// the rewrite has no version floor.
        /// </summary>
        [JsonPropertyName("minimumSqlServerMajorVersion")]
        public int? MinimumSqlServerMajorVersion { get; set; }

        /// <summary>
        /// Minimum database compatibility level, e.g. 150 for SQL Server 2019 behaviour.
        /// </summary>
        /// <remarks>
        /// Nearly always the right gate in preference to
        /// <see cref="MinimumSqlServerMajorVersion"/>. Sampling the estate found production
        /// databases pinned to compatibility level 110 on a current Azure SQL engine: the
        /// product version says 2012-and-later features are available, while the optimiser
        /// is still generating 2012-era plans. Any rewrite that depends on optimiser
        /// behaviour rather than on syntax availability must gate here.
        /// </remarks>
        [JsonPropertyName("minimumCompatibilityLevel")]
        public int? MinimumCompatibilityLevel { get; set; }

        /// <summary>
        /// Indexes that must exist for the rewrite to be a win rather than a regression.
        /// Names are matched against <c>sys.indexes</c>. A rewrite whose required index is
        /// missing is skipped, so shipping the rewrite before the index is deployed is safe.
        /// </summary>
        [JsonPropertyName("requiredIndexes")]
        public string[] RequiredIndexes { get; set; }

        /// <summary>True when no precondition is set and the rewrite always applies.</summary>
        [JsonIgnore]
        public bool IsUnconditional
        {
            get
            {
                return !RequiresCaseInsensitiveCollation
                    && !RequiresAccentInsensitiveCollation
                    && !MinimumSqlServerMajorVersion.HasValue
                    && !MinimumCompatibilityLevel.HasValue
                    && (RequiredIndexes == null || RequiredIndexes.Length == 0);
            }
        }
    }
}
