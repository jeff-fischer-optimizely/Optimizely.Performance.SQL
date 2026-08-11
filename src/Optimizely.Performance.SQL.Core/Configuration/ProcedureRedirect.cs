using System.Text.Json.Serialization;

namespace Optimizely.Performance.SQL.Configuration
{
    /// <summary>
    /// Points a stored procedure call at a versioned copy carrying the optimised body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The procedure Optimizely ships is never altered. The optimised body is deployed
    /// alongside it under a new name, and the shim changes only which name the CMS calls.
    /// Everything else about the call — parameters, directions, return code, result set
    /// shape — is untouched, so the redirect is invisible to the caller.
    /// </para>
    /// <para>
    /// This also gives the mechanism its failure mode for free: if the replacement was
    /// never deployed, or a CMS upgrade has moved the original underneath us, no redirect
    /// happens and the shipped procedure runs.
    /// </para>
    /// </remarks>
    public sealed class ProcedureRedirect
    {
        /// <summary>
        /// Procedure the CMS calls today, e.g. <c>netContentLoad</c>. Matched without
        /// regard to case, schema prefix or bracket quoting.
        /// </summary>
        [JsonPropertyName("originalName")]
        public string OriginalName { get; set; }

        /// <summary>
        /// Procedure to call instead, e.g. <c>netContentLoad_optiperf_v1</c>. Deployed by
        /// the installer; the shim never creates it.
        /// </summary>
        [JsonPropertyName("replacementName")]
        public string ReplacementName { get; set; }

        /// <summary>
        /// Hash of the original procedure body this replacement was written against, as
        /// produced by the sync tool from <c>sys.sql_modules.definition</c>.
        /// </summary>
        /// <remarks>
        /// Checked at probe time against what is actually in the database. A mismatch means
        /// Optimizely has patched the procedure since the replacement was approved, so the
        /// replacement is no longer known to be equivalent and the redirect is withdrawn.
        /// This is a fact check against the live schema, not an approval decision.
        /// </remarks>
        [JsonPropertyName("originalBodyHash")]
        public string OriginalBodyHash { get; set; }
    }
}
