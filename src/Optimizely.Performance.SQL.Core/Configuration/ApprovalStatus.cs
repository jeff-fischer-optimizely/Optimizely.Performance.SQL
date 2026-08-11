namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Lifecycle state of a rewrite. Only <see cref="Approved"/> statements are ever
    /// substituted at runtime; everything else is loaded but inert, so an in-progress
    /// rewrite can sit in configuration without any risk of reaching production traffic.
    /// </summary>
    public enum ApprovalStatus
    {
        /// <summary>Authored but not reviewed. Never applied.</summary>
        Draft = 0,

        /// <summary>Under review. Never applied.</summary>
        InReview = 1,

        /// <summary>Reviewed and signed off. Applied when preconditions are satisfied.</summary>
        Approved = 2,

        /// <summary>Explicitly rejected. Never applied, retained for audit history.</summary>
        Rejected = 3,

        /// <summary>Previously approved, since withdrawn. Never applied.</summary>
        Revoked = 4
    }
}
