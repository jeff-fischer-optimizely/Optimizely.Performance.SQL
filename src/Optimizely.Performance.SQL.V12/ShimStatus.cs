using Optimizely.Performance.SQL.Interception;

namespace Optimizely.Performance.SQL.V12
{
    /// <summary>
    /// What installation did, in a form that can be logged or surfaced on a health page.
    /// </summary>
    /// <remarks>
    /// The shim fails open, which means a site can be running perfectly while rewriting
    /// nothing at all. Something has to be able to say so out loud, or the first anyone
    /// notices is that the queries never got faster.
    /// </remarks>
    public sealed class ShimStatus
    {
        /// <summary>The state before any installation attempt.</summary>
        public static readonly ShimStatus NotInstalled = new ShimStatus(false, "not installed", null);

        private ShimStatus(bool installed, string message, RewriteHost host)
        {
            Installed = installed;
            Message = message;
            StatementCount = host == null ? 0 : host.StatementCount;
            ConfigurationPath = host == null ? null : host.ConfigurationPath;
            LoadError = host == null ? null : host.LoadError;
        }

        /// <summary>True when the diagnostic subscription is active.</summary>
        public bool Installed { get; }

        /// <summary>Human-readable summary, including the reason when not installed.</summary>
        public string Message { get; }

        /// <summary>Approved statements loaded. Zero means installed but with nothing to do.</summary>
        public int StatementCount { get; }

        /// <summary>Absolute path configuration was read from.</summary>
        public string ConfigurationPath { get; }

        /// <summary>Why configuration failed to load, or null.</summary>
        public string LoadError { get; }

        internal static ShimStatus Success(RewriteHost host)
        {
            return new ShimStatus(true, "installed", host);
        }

        /// <summary>Installation was declined deliberately. Not an error.</summary>
        internal static ShimStatus Skipped(string reason)
        {
            return new ShimStatus(false, "skipped: " + reason, null);
        }

        /// <summary>Installation was attempted and did not work.</summary>
        internal static ShimStatus Failed(string reason)
        {
            return new ShimStatus(false, "failed: " + reason, null);
        }

        public override string ToString()
        {
            if (!Installed)
            {
                return Message;
            }

            var text = string.Format(
                "installed: subscribed to the SqlClient diagnostic source, {0} approved statements from {1}",
                StatementCount,
                ConfigurationPath ?? "(no configuration path)");

            if (LoadError != null)
            {
                text += ", configuration error: " + LoadError;
            }

            return text;
        }
    }
}
