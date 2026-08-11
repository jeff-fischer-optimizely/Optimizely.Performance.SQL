using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// A single reviewed statement rewrite: the original text as the CMS emits it, the
    /// approved replacement, the evidence behind it, and the sign-off that authorises
    /// the shim to substitute one for the other at runtime.
    /// </summary>
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

        /// <summary>
        /// Normalized fingerprint of <see cref="OriginalSql"/>. This is the runtime
        /// lookup key. It is derived, not hand-written: the sync tool recomputes it from
        /// <see cref="OriginalSql"/> and fails the build when the two disagree.
        /// </summary>
        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; }

        /// <summary>CMS versions this rewrite is valid for.</summary>
        [JsonPropertyName("appliesTo")]
        public CmsVersion AppliesTo { get; set; } = CmsVersion.All;

        /// <summary>Approval state. Anything other than <see cref="ApprovalStatus.Approved"/> is inert.</summary>
        [JsonPropertyName("status")]
        public ApprovalStatus Status { get; set; } = ApprovalStatus.Draft;

        /// <summary>Who signed off. Required by the CI gate when <see cref="Status"/> is Approved.</summary>
        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }

        /// <summary>ISO-8601 sign-off date. Required by the CI gate when Approved.</summary>
        [JsonPropertyName("approvedOn")]
        public string ApprovedOn { get; set; }

        /// <summary>
        /// Repo-relative path to the markdown approval record, e.g.
        /// <c>approvals/OPT-0007-content-children.md</c>. The gate verifies the file exists
        /// and that its front matter agrees with this entry.
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

        /// <summary>Server-side facts that must hold before any replacement is applied.</summary>
        [JsonPropertyName("preconditions")]
        public RewritePreconditions Preconditions { get; set; }

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
            return Enabled
                && Status == ApprovalStatus.Approved
                && (AppliesTo & version) != CmsVersion.None;
        }
    }
}
