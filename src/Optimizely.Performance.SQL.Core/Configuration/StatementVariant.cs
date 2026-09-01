using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// One pre-analyzed rewrite of a statement, guarded by parameter conditions.
    /// </summary>
    /// <remarks>
    /// Variants are evaluated in declaration order and the first whose conditions all
    /// hold is used, so order the specific ones before the general ones. A statement
    /// with no matching variant falls back to
    /// <see cref="ApprovedStatement.RewrittenSql"/>; if that is also absent the original
    /// text is left untouched.
    /// </remarks>
    public sealed class StatementVariant
    {
        /// <summary>Stable identifier, unique within the owning statement. Used in logs and approval docs.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Human-readable summary of when this variant applies and why it is faster.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// Conditions that must <em>all</em> hold. An empty or null array makes this an
        /// unconditional catch-all, which only makes sense as the last variant.
        /// </summary>
        [JsonPropertyName("when")]
        public VariantCondition[] When { get; set; }

        /// <summary>The replacement statement text.</summary>
        [JsonPropertyName("sql")]
        public string Sql { get; set; }

        /// <summary>
        /// Parameters the original command supplies that this variant's SQL no longer
        /// references, stripped from the command before execution.
        /// </summary>
        /// <remarks>
        /// Not a correctness requirement: SqlClient sends a parameterised batch through
        /// <c>sp_executesql</c>, which tolerates a declared parameter the batch never
        /// mentions. It is about the plan. Leaving the parameter declared keeps it in the
        /// <c>sp_executesql</c> signature, so the rewritten statement caches under a
        /// different key than the same text without it and is exposed to sniffing on a
        /// value it no longer uses.
        /// </remarks>
        [JsonPropertyName("dropsParameters")]
        public string[] DropsParameters { get; set; }
    }
}
