using System;
using System.Configuration;
using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL.V11
{
    /// <summary>
    /// Reads <see cref="RewriteOptions"/> from <c>web.config</c>'s <c>appSettings</c>.
    /// </summary>
    /// <remarks>
    /// CMS 11 predates the options pattern, and the shim installs before EPiServer's own
    /// initialization has run, so there is no configuration system available yet.
    /// <c>appSettings</c> is what exists that early.
    /// </remarks>
    public static class AppSettingsOptions
    {
        /// <summary>Prefix for every key this shim reads.</summary>
        public const string Prefix = "optimizely:performance-sql:";

        /// <summary>
        /// Builds options from configuration, falling back to defaults for anything absent
        /// or unparseable.
        /// </summary>
        /// <remarks>
        /// A malformed setting is ignored rather than fatal. This runs during application
        /// start, where throwing takes the site down before a single request is served.
        /// </remarks>
        public static RewriteOptions Read()
        {
            var options = new RewriteOptions();

            try
            {
                options.Enabled = Bool("enabled", options.Enabled);
                options.ShadowMode = Bool("shadowMode", options.ShadowMode);
                options.ReloadOnChange = Bool("reloadOnChange", options.ReloadOnChange);
                options.AnnotateRewrittenSql = Bool("annotateRewrittenSql", options.AnnotateRewrittenSql);
                options.ProbeDatabaseCapabilities = Bool("probeDatabaseCapabilities", options.ProbeDatabaseCapabilities);

                var path = String("configurationPath");

                if (path != null)
                {
                    options.ConfigurationPath = path;
                }
            }
            catch (Exception)
            {
                // An unreadable configuration section must not stop the site starting.
            }

            return options;
        }

        private static string String(string key)
        {
            var value = ConfigurationManager.AppSettings[Prefix + key];

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool Bool(string key, bool fallback)
        {
            var value = String(key);

            bool parsed;

            return value != null && bool.TryParse(value, out parsed) ? parsed : fallback;
        }
    }
}
