using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// A single reviewed rewrite: the original as the CMS emits it, the approved
    /// replacement, and the server-side facts that must hold before the substitution is
    /// applied.
    /// </summary>
    /// <remarks>
    /// Approval happens offline, in the markdown record under <c>approvals/</c>, and is
    /// projected into configuration by the sync tool. Everything present here is therefore
    /// approved by construction: there is no approval state to evaluate at runtime and no
    /// code path that could execute an unapproved statement. The sign-off fields below are
    /// inert provenance, carried so that support can trace a live rewrite back to the
    /// record that authorised it.
    /// </remarks>
    public sealed class ApprovedStatement
    {
        /// <summary>
        /// Stable identifier, e.g. <c>OPT-0007</c>. This is the join key between this
        /// entry and its approval document under <c>approvals/</c>, and it must never be
        /// reused once issued.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>Short description of the query and the problem being fixed.</summary>
        [JsonPropertyName("title")]
        public string Title { get; set; }

        /// <summary>Whether this entry replaces statement text or redirects a procedure call.</summary>
        [JsonPropertyName("kind")]
        public RewriteKind Kind { get; set; } = RewriteKind.StatementRewrite;

        /// <summary>
        /// Normalized fingerprint of <see cref="OriginalSql"/>, and the runtime lookup key
        /// for <see cref="RewriteKind.StatementRewrite"/> entries. It is derived, not
        /// hand-written: the sync tool recomputes it from <see cref="OriginalSql"/> and
        /// fails the build when the two disagree.
        /// </summary>
        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; }

        /// <summary>CMS versions this rewrite is valid for.</summary>
        [JsonPropertyName("appliesTo")]
        public CmsVersion AppliesTo { get; set; } = CmsVersion.All;

        /// <summary>Who signed off. Provenance only; never evaluated.</summary>
        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }

        /// <summary>ISO-8601 sign-off date. Provenance only; never evaluated.</summary>
        [JsonPropertyName("approvedOn")]
        public string ApprovedOn { get; set; }

        /// <summary>
        /// Repo-relative path to the markdown approval record, e.g.
        /// <c>approvals/OPT-0007-content-children.md</c>. Provenance only; never evaluated.
        /// </summary>
        [JsonPropertyName("approvalDocument")]
        public string ApprovalDocument { get; set; }

        /// <summary>The statement exactly as the CMS emits it today.</summary>
        [JsonPropertyName("originalSql")]
        public string OriginalSql { get; set; }

        /// <summary>
        /// Default replacement, used when no <see cref="Variants"/> entry matches.
        /// May be null for statements that are entirely variant-driven.
        /// </summary>
        [JsonPropertyName("rewrittenSql")]
        public string RewrittenSql { get; set; }

        /// <summary>
        /// Condition-guarded replacements, evaluated in order. This is what lets a
        /// statement shed null-check predicates and OR branches without dynamic SQL.
        /// </summary>
        [JsonPropertyName("variants")]
        public StatementVariant[] Variants { get; set; }

        /// <summary>
        /// Procedure redirect details. Required when <see cref="Kind"/> is
        /// <see cref="RewriteKind.ProcedureRedirect"/>, ignored otherwise.
        /// </summary>
        [JsonPropertyName("procedure")]
        public ProcedureRedirect Procedure { get; set; }

        /// <summary>Server-side facts that must hold before any replacement is applied.</summary>
        [JsonPropertyName("preconditions")]
        public RewritePreconditions Preconditions { get; set; }

        /// <summary>
        /// How much could go wrong if this rewrite is applied somewhere nobody tested it.
        /// Descriptive; never evaluated. See <see cref="RewriteRisk"/>.
        /// </summary>
        [JsonPropertyName("risk")]
        public RewriteRisk Risk { get; set; } = RewriteRisk.Unclassified;

        /// <summary>
        /// Why this entry sits in its tier — the specific thing that could differ, or the
        /// specific fact that rules it out.
        /// </summary>
        /// <remarks>
        /// A tier without a reason is an assertion, and assertions do not survive review.
        /// This is the field that makes the classification auditable a year from now, when
        /// whoever assigned it has forgotten what they were looking at.
        /// </remarks>
        [JsonPropertyName("riskRationale")]
        public string RiskRationale { get; set; }

        /// <summary>
        /// How much database work this statement accounts for across the surveyed fleet.
        /// Provenance only; never evaluated. See <see cref="RewriteImpact"/>.
        /// </summary>
        [JsonPropertyName("impact")]
        public RewriteImpact Impact { get; set; }

        /// <summary>
        /// Indexes recommended alongside this rewrite, as executable DDL. These are
        /// documentation and deployment input; the shim never runs DDL itself.
        /// </summary>
        [JsonPropertyName("recommendedIndexes")]
        public string[] RecommendedIndexes { get; set; }

        /// <summary>Reviewer notes: measured effect, plan shape, residual risk.</summary>
        [JsonPropertyName("notes")]
        public string Notes { get; set; }

        /// <summary>
        /// Per-entry kill switch. An approved entry can be disabled without losing its
        /// approval history, which is what you want during an incident.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>True when this entry is eligible to run against the given CMS version.</summary>
        public bool IsActiveFor(CmsVersion version)
        {
            return Enabled && (AppliesTo & version) != CmsVersion.None;
        }
    }
}
