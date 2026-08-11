using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Outcome of asking the registry what to do with a statement.
    /// </summary>
    public sealed class RewriteResult
    {
        /// <summary>Singleton for "leave this statement alone", the overwhelmingly common answer.</summary>
        public static readonly RewriteResult NoChange = new RewriteResult();

        private RewriteResult()
        {
            Reason = RewriteOutcome.NotMatched;
        }

        private RewriteResult(
            ApprovedStatement statement,
            StatementVariant variant,
            string sql,
            RewriteOutcome reason)
        {
            Statement = statement;
            Variant = variant;
            Sql = sql;
            Reason = reason;
        }

        /// <summary>The matched entry, or null when nothing matched.</summary>
        public ApprovedStatement Statement { get; }

        /// <summary>The selected variant, or null when the default rewrite was used.</summary>
        public StatementVariant Variant { get; }

        /// <summary>Replacement text. Null unless <see cref="ShouldReplace"/> is true.</summary>
        public string Sql { get; }

        /// <summary>Why this result came out the way it did.</summary>
        public RewriteOutcome Reason { get; }

        /// <summary>True when the caller should swap in <see cref="Sql"/>.</summary>
        public bool ShouldReplace
        {
            get { return Reason == RewriteOutcome.Rewritten && Sql != null; }
        }

        /// <summary>Parameters to remove from the command before execution.</summary>
        public string[] DroppedParameters
        {
            get { return Variant?.DropsParameters; }
        }

        /// <summary>Identifier for logs: the entry id, plus the variant id when one was chosen.</summary>
        public string DisplayId
        {
            get
            {
                if (Statement == null)
                {
                    return "(none)";
                }

                return Variant == null || string.IsNullOrEmpty(Variant.Id)
                    ? Statement.Id
                    : Statement.Id + "/" + Variant.Id;
            }
        }

        internal static RewriteResult Rewritten(ApprovedStatement statement, StatementVariant variant, string sql)
        {
            return new RewriteResult(statement, variant, sql, RewriteOutcome.Rewritten);
        }

        internal static RewriteResult Suppressed(ApprovedStatement statement, RewriteOutcome reason)
        {
            return new RewriteResult(statement, null, null, reason);
        }
    }

    /// <summary>
    /// Why a statement was or was not rewritten. Every non-rewrite reason is reported to
    /// observers, because "matched but skipped" is the interesting diagnostic case.
    /// </summary>
    public enum RewriteOutcome
    {
        /// <summary>No approved entry has this fingerprint.</summary>
        NotMatched = 0,

        /// <summary>Matched and replaced.</summary>
        Rewritten = 1,

        /// <summary>Matched, but the entry is not Approved, is disabled, or targets another CMS version.</summary>
        NotActive = 2,

        /// <summary>Matched, but the database does not satisfy the entry's preconditions.</summary>
        PreconditionsNotMet = 3,

        /// <summary>Matched, but no variant's conditions held and there is no default rewrite.</summary>
        NoVariantMatched = 4,

        /// <summary>Matched and resolved, but shadow mode is on so the original text was kept.</summary>
        Shadowed = 5,

        /// <summary>The shim is switched off.</summary>
        Disabled = 6
    }
}
