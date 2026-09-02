using System;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.V11.Patching;

namespace Optimizely.Performance.SQL.V11
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
        public static readonly ShimStatus NotInstalled =
            new ShimStatus(false, "not installed", null, null);

        private ShimStatus(bool installed, string message, PatchReport report, RewriteHost host)
        {
            Installed = installed;
            Message = message;
            PatchedMethods = report == null ? Array.Empty<string>() : report.PatchedMethods;
            Failures = report == null ? Array.Empty<string>() : report.Failures;
            StatementCount = host == null ? 0 : host.StatementCount;
            ConfigurationPath = host == null ? null : host.ConfigurationPath;
            LoadError = host == null ? null : host.LoadError;
        }

        /// <summary>True when commands are being intercepted.</summary>
        public bool Installed { get; }

        /// <summary>Human-readable summary, including the reason when not installed.</summary>
        public string Message { get; }

        /// <summary>Execution methods now under interception.</summary>
        public string[] PatchedMethods { get; }

        /// <summary>Execution methods the runtime refused, each with the reason.</summary>
        public string[] Failures { get; }

        /// <summary>Approved statements loaded. Zero means installed but with nothing to do.</summary>
        public int StatementCount { get; }

        /// <summary>Absolute path configuration was read from.</summary>
        public string ConfigurationPath { get; }

        /// <summary>Why configuration failed to load, or null.</summary>
        public string LoadError { get; }

        internal static ShimStatus Success(PatchReport report, RewriteHost host)
        {
            return new ShimStatus(true, "installed", report, host);
        }

        /// <summary>Installation was declined deliberately. Not an error.</summary>
        internal static ShimStatus Skipped(string reason)
        {
            return new ShimStatus(false, "skipped: " + reason, null, null);
        }

        /// <summary>Installation was attempted and did not work.</summary>
        internal static ShimStatus Failed(string reason)
        {
            return new ShimStatus(false, "failed: " + reason, null, null);
        }

        public override string ToString()
        {
            if (!Installed)
            {
                return Message;
            }

            var text = string.Format(
                "installed: {0} execution methods patched, {1} approved statements from {2}",
                PatchedMethods.Length,
                StatementCount,
                ConfigurationPath ?? "(no configuration path)");

            if (Failures.Length != 0)
            {
                text += string.Format(", {0} method(s) not patched: {1}", Failures.Length, string.Join("; ", Failures));
            }

            if (LoadError != null)
            {
                text += ", configuration error: " + LoadError;
            }

            return text;
        }
    }
}
