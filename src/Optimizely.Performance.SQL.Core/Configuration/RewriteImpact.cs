using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// How much database work the original statement accounts for, as measured by the fleet
    /// survey.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carried so that the catalogue can be ordered by size of the win rather than by
    /// whatever order the statements were reviewed in. Read alongside
    /// <see cref="ApprovedStatement.Risk"/>: the work worth doing first is high impact and
    /// low risk, and neither number alone identifies it.
    /// </para>
    /// <para>
    /// Deliberately expressed in logical reads, executions and database counts. The survey
    /// also produces commercial figures; those are customer-attributable and stay in the
    /// survey rather than in a file that ships.
    /// </para>
    /// <para>
    /// Every field is provenance. Nothing in the shim reads these to decide anything, and
    /// they are snapshots — figures measured against one fleet on one date, not a promise
    /// about the database currently executing the statement.
    /// </para>
    /// </remarks>
    public sealed class RewriteImpact
    {
        /// <summary>Mean logical reads per execution at the time of the survey.</summary>
        [JsonPropertyName("averageLogicalReads")]
        public double AverageLogicalReads { get; set; }

        /// <summary>Logical reads attributed to this statement across the survey window.</summary>
        [JsonPropertyName("totalLogicalReads")]
        public double TotalLogicalReads { get; set; }

        /// <summary>
        /// How many surveyed databases run this statement at all.
        /// </summary>
        /// <remarks>
        /// The prevalence figure, and the one that separates a rewrite worth shipping to
        /// everybody from a rewrite that matters to a single customer.
        /// </remarks>
        [JsonPropertyName("databasesAffected")]
        public int DatabasesAffected { get; set; }

        /// <summary>Executions observed across the fleet during the survey window.</summary>
        [JsonPropertyName("totalExecutions")]
        public long TotalExecutions { get; set; }

        /// <summary>
        /// The survey's own diagnosis of why the statement is inefficient, carried verbatim.
        /// </summary>
        /// <remarks>
        /// Kept unedited on purpose. It is the input the rewrite was authored against, so
        /// preserving it lets a reviewer check the rewrite against the reasoning that
        /// prompted it rather than against a later paraphrase of it.
        /// </remarks>
        [JsonPropertyName("surveyDiagnosis")]
        public string SurveyDiagnosis { get; set; }

        /// <summary>Which survey these figures came from, e.g. <c>all_cms_query_costs 2026-08-11</c>.</summary>
        [JsonPropertyName("source")]
        public string Source { get; set; }
    }
}
