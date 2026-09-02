using System.Collections.Generic;
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
        /// Highest database compatibility level the rewrite still applies at, inclusive.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The counterpart to <see cref="MinimumCompatibilityLevel"/>, and the gate for
        /// rewrites that hand-implement something a later optimiser does on its own. Table
        /// variable deferred compilation is the motivating case: below level 150 a table
        /// variable is estimated at one row and the plan is sized for a batch that does not
        /// exist, so forcing a recompile is a large win; at 150 and above the engine already
        /// defers compilation and the same hint buys nothing but compile CPU on every call.
        /// </para>
        /// <para>
        /// Expressing that as a ceiling rather than as two separate entries means the estate
        /// upgrades itself out of the rewrite. A database moved from 140 to 150 stops
        /// receiving it on the next capability probe, with no configuration change and no
        /// deployment.
        /// </para>
        /// </remarks>
        [JsonPropertyName("maximumCompatibilityLevel")]
        public int? MaximumCompatibilityLevel { get; set; }

        /// <summary>
        /// Indexes that must exist for the rewrite to be a win rather than a regression.
        /// Names are matched against <c>sys.indexes</c>. A rewrite whose required index is
        /// missing is skipped, so shipping the rewrite before the index is deployed is safe.
        /// </summary>
        [JsonPropertyName("requiredIndexes")]
        public string[] RequiredIndexes { get; set; }

        /// <summary>
        /// Other objects the replacement procedure calls, which must therefore be deployed
        /// before the redirect can fire.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The registry already refuses to redirect to a replacement that is not deployed.
        /// That check covers the procedure named in the redirect and stops there, which is
        /// not enough once a replacement calls another one. The catalog entry loader is the
        /// case in hand: its versioned copy calls the versioned components procedure, so a
        /// database with the first deployed and the second missing would pass every gate and
        /// then fail at execution -- turning a fail-open design into a fail-loud one on
        /// exactly the half-finished deployment it exists to tolerate.
        /// </para>
        /// <para>
        /// Naming the dependency here restores the property that a partial deployment is
        /// merely inert. Names are matched the same way procedure names are everywhere else,
        /// so schema qualification and bracketing are optional.
        /// </para>
        /// </remarks>
        [JsonPropertyName("requiredProcedures")]
        public string[] RequiredProcedures { get; set; }

        /// <summary>
        /// Other procedures that must be present <em>and</em> hash to a given body, keyed by
        /// procedure name with the expected <see cref="Fingerprinting.ModuleHash"/> as the value.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A redirect normally identifies which product version it is for by hashing the
        /// procedure it replaces. That fails when the procedure is identical across versions
        /// but something it calls is not. The catalog entry loader is exactly that: byte for
        /// byte the same in Commerce 14 and 15, while the components procedure it calls
        /// returns an extra Merchant result set in 14. Two replacements are needed and the
        /// loader's own hash cannot choose between them.
        /// </para>
        /// <para>
        /// Naming the callee and its expected body here moves the discriminator to where the
        /// difference actually is. It also makes the two entries mutually exclusive by
        /// construction rather than by ordering, so deploying both sets of scripts to one
        /// database still resolves correctly.
        /// </para>
        /// </remarks>
        [JsonPropertyName("requiredProcedureBodies")]
        public Dictionary<string, string> RequiredProcedureBodies { get; set; }

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
                    && !MaximumCompatibilityLevel.HasValue
                    && (RequiredIndexes == null || RequiredIndexes.Length == 0)
                    && (RequiredProcedures == null || RequiredProcedures.Length == 0)
                    && (RequiredProcedureBodies == null || RequiredProcedureBodies.Count == 0);
            }
        }
    }
}
