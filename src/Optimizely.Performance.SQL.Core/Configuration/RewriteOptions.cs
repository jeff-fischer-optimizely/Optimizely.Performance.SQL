using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL
{
    /// <summary>
    /// Host-supplied switches controlling how the shim behaves at runtime.
    /// </summary>
    public sealed class RewriteOptions
    {
        /// <summary>
        /// Master kill switch. When false the decorators still sit in the call path but
        /// pass every statement through untouched, so the shim can be disabled by
        /// configuration without a redeploy.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Which CMS version's rewrites to load. Set by the V11/V12 adapter.
        /// </summary>
        public CmsVersion Version { get; set; } = CmsVersion.All;

        /// <summary>
        /// Report what would be substituted without actually substituting it. Intended
        /// for a first production soak: observers see every match, traffic sees none.
        /// </summary>
        public bool ShadowMode { get; set; }

        /// <summary>
        /// Path to the generated approved-SQL document. Relative paths resolve against
        /// the application base directory.
        /// </summary>
        public string ConfigurationPath { get; set; } = "config/approved-sql.json";

        /// <summary>
        /// Re-read <see cref="ConfigurationPath"/> when it changes on disk. Useful in
        /// staging; leave off in production where config arrives with a deploy.
        /// </summary>
        public bool ReloadOnChange { get; set; }

        /// <summary>
        /// Append a marker comment naming the rewrite id to every substituted statement.
        /// It makes the shim's effect obvious in Query Store, extended events and
        /// execution plans, at the cost of a distinct plan from the unrewritten text.
        /// </summary>
        public bool AnnotateRewrittenSql { get; set; } = true;

        /// <summary>
        /// Probe the server for collation, version and index presence so
        /// <see cref="RewritePreconditions"/> can be evaluated. When false, any rewrite
        /// carrying a precondition is skipped, since it cannot be shown to be safe.
        /// </summary>
        public bool ProbeDatabaseCapabilities { get; set; } = true;
    }
}
