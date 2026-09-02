using System;
using System.Diagnostics;
using System.Web;
using Optimizely.Performance.SQL.V11;

[assembly: PreApplicationStartMethod(typeof(PerformanceSqlStartup), nameof(PerformanceSqlStartup.Start))]

namespace Optimizely.Performance.SQL.V11
{
    /// <summary>
    /// Installs the shim automatically, as early as ASP.NET allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>PreApplicationStartMethod</c> runs before <c>Application_Start</c> and well before
    /// EPiServer's initialization system, so the patches are in place before the CMS issues
    /// its first query. That matters more than it looks: an Optimizely site does a great
    /// deal of its slowest database work while starting up, and a shim installed from an
    /// initialization module would watch all of it go past unrewritten.
    /// </para>
    /// <para>
    /// Automatic because the alternative is a shim that is installed on the machines
    /// somebody remembered to configure. Set
    /// <c>optimizely:performance-sql:enabled</c> to <c>false</c> in <c>appSettings</c> to
    /// suppress it, or call <see cref="PerformanceSqlShim"/>'s <c>Install</c> overload
    /// yourself to supply options in code — the installer is idempotent, so doing both is
    /// safe and the explicit call simply reloads configuration.
    /// </para>
    /// </remarks>
    public static class PerformanceSqlStartup
    {
        /// <summary>
        /// Entry point for the <c>PreApplicationStartMethod</c> attribute.
        /// </summary>
        /// <remarks>
        /// Never throws. An exception from a pre-application-start method prevents the
        /// application from starting at all, which is a spectacular way for a performance
        /// optimisation to fail.
        /// </remarks>
        public static void Start()
        {
            try
            {
                var status = PerformanceSqlShim.Install();

                if (!status.Installed)
                {
                    Trace.WriteLine("[Optimizely.Performance.SQL] " + status);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[Optimizely.Performance.SQL] startup failed, continuing without rewriting: " + ex);
            }
        }
    }
}
