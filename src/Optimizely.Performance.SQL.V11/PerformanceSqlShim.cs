using System;
using System.Diagnostics;
using HarmonyLib;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.V11.Patching;

namespace Optimizely.Performance.SQL.V11
{
    /// <summary>
    /// Installs approved-SQL rewriting into a CMS 11 site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CMS 11 runs on .NET Framework, where <c>System.Data.SqlClient</c> is in-box and has
    /// no <c>DiagnosticSource</c> — the mechanism the CMS 12 adapter uses does not exist
    /// here. The only remaining interception point that survives EPiServer's 25 hard casts
    /// to <c>SqlCommand</c> is to patch the command's execution methods in place, so that
    /// is what this does.
    /// </para>
    /// <para>
    /// Installation is best-effort by design. If the runtime refuses to patch, the site
    /// starts and runs unrewritten, and <see cref="Status"/> says why. A performance shim
    /// is never worth an outage.
    /// </para>
    /// </remarks>
    public static class PerformanceSqlShim
    {
        private const string HarmonyId = "com.optimizely.performance.sql.v11";

        private static readonly object Gate = new object();
        private static Harmony _harmony;
        private static RewriteHost _host;

        /// <summary>Outcome of the last <see cref="Install(RewriteOptions, IRewriteObserver, string)"/> call.</summary>
        public static ShimStatus Status { get; private set; } = ShimStatus.NotInstalled;

        /// <summary>True when the execution methods are patched and a context is attached.</summary>
        public static bool IsInstalled
        {
            get { return Status.Installed; }
        }

        /// <summary>
        /// The live rewrite context, or null when not installed. Exposed for diagnostics and
        /// for tests; hosts should not need it.
        /// </summary>
        public static RewriteHost Host
        {
            get { return _host; }
        }

        /// <summary>Number of commands whose text has been substituted.</summary>
        public static long Applied
        {
            get { return SqlCommandPatch.Applied; }
        }

        /// <summary>Number of substitutions undone. Should track <see cref="Applied"/> exactly.</summary>
        public static long Restored
        {
            get { return SqlCommandPatch.Restored; }
        }

        /// <summary>
        /// Number of times undoing a substitution threw. Any non-zero value means a command
        /// was left holding text its owner did not set, and is a defect.
        /// </summary>
        public static long RestoreFailures
        {
            get { return SqlCommandPatch.RestoreFailures; }
        }

        /// <summary>
        /// Reads configuration from <c>appSettings</c> and installs. This is what the
        /// automatic startup hook calls.
        /// </summary>
        public static ShimStatus Install()
        {
            return Install(AppSettingsOptions.Read(), null, null);
        }

        /// <summary>
        /// Patches <c>SqlCommand</c> and attaches a context built from
        /// <paramref name="options"/>.
        /// </summary>
        /// <param name="options">Host configuration. Defaults are used when null.</param>
        /// <param name="observer">Optional sink for per-statement rewrite decisions.</param>
        /// <param name="baseDirectory">
        /// Root for a relative configuration path. Defaults to the application base
        /// directory, which under ASP.NET is the site root rather than <c>bin</c>.
        /// </param>
        /// <remarks>
        /// Idempotent: calling it again on an installed shim reloads configuration and
        /// returns the existing status rather than patching a second time.
        /// </remarks>
        public static ShimStatus Install(
            RewriteOptions options = null,
            IRewriteObserver observer = null,
            string baseDirectory = null)
        {
            lock (Gate)
            {
                if (_harmony != null)
                {
                    if (_host != null)
                    {
                        _host.Reload();
                    }

                    return Status;
                }

                var effective = options ?? new RewriteOptions();

                if (!effective.Enabled)
                {
                    return Status = ShimStatus.Skipped("disabled by configuration");
                }

                if ((effective.Version & CmsVersion.V11) == 0)
                {
                    return Status = ShimStatus.Skipped("configuration does not target CMS 11");
                }

                RewriteHost host;

                try
                {
                    host = RewriteHost.Create(effective, observer, baseDirectory);
                }
                catch (Exception ex)
                {
                    return Status = ShimStatus.Failed("could not load configuration: " + ex.Message);
                }

                Harmony harmony;
                PatchReport report;

                try
                {
                    harmony = new Harmony(HarmonyId);
                    report = SqlCommandPatcher.Apply(harmony);
                }
                catch (Exception ex)
                {
                    host.Dispose();

                    return Status = ShimStatus.Failed("could not patch SqlCommand: " + ex.Message);
                }

                if (!report.AnyPatched)
                {
                    host.Dispose();

                    return Status = ShimStatus.Failed(
                        "no execution method could be patched" + Describe(report.Failures));
                }

                // Context last. Until it is set the patches are inert, so a half-applied
                // patch set can never rewrite anything.
                _host = host;
                _harmony = harmony;
                SqlCommandPatch.Context = host.Context;

                Status = ShimStatus.Success(report, host);

                Trace.WriteLine("[Optimizely.Performance.SQL] " + Status);

                return Status;
            }
        }

        /// <summary>
        /// Detaches the context and removes the patches.
        /// </summary>
        /// <remarks>
        /// The context is cleared first, so commands already inside a patched method stop
        /// being rewritten immediately and the ones mid-execution still find their rewrite
        /// to undo — the finalizer restores from state it captured, not from the context.
        /// </remarks>
        public static void Uninstall()
        {
            lock (Gate)
            {
                SqlCommandPatch.Context = null;

                var harmony = _harmony;
                _harmony = null;

                if (harmony != null)
                {
                    try
                    {
                        SqlCommandPatcher.Remove(harmony);
                    }
                    catch (Exception)
                    {
                        // Leaving the patches in place is harmless: without a context they
                        // return immediately.
                    }
                }

                var host = _host;
                _host = null;

                if (host != null)
                {
                    host.Dispose();
                }

                Status = ShimStatus.NotInstalled;
            }
        }

        private static string Describe(string[] failures)
        {
            return failures.Length == 0 ? string.Empty : " (" + string.Join("; ", failures) + ")";
        }
    }
}
