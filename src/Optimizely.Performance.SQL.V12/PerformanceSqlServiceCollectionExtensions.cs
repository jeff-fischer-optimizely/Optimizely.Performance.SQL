using System;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.V12;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Registration for the CMS 12 shim.
    /// </summary>
    public static class PerformanceSqlServiceCollectionExtensions
    {
        /// <summary>
        /// Installs approved-SQL rewriting and registers its diagnostics for injection.
        /// </summary>
        /// <param name="services">The application's service collection.</param>
        /// <param name="configure">Optional callback to adjust options before installing.</param>
        /// <param name="observer">Optional sink for per-statement rewrite decisions.</param>
        /// <remarks>
        /// <para>
        /// Installation happens <em>here</em>, while services are being registered, rather
        /// than when something first resolves the shim from the container. That is
        /// deliberate. Interception only sees commands issued after it is in place, and by
        /// the time a hosted service or an initialization module runs, EPiServer has already
        /// done a substantial amount of its startup querying — precisely the slow work worth
        /// rewriting.
        /// </para>
        /// <para>
        /// Earlier still is better. Nothing stops you calling
        /// <see cref="PerformanceSqlShim.Install(RewriteOptions, IRewriteObserver, string)"/>
        /// on the first line of <c>Program.cs</c>; this method is idempotent and will simply
        /// reload configuration if the shim is already installed.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddOptimizelyPerformanceSql(
            this IServiceCollection services,
            Action<RewriteOptions> configure = null,
            IRewriteObserver observer = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var options = new RewriteOptions();

            if (configure != null)
            {
                configure(options);
            }

            var status = PerformanceSqlShim.Install(options, observer);

            services.AddSingleton(status);
            services.AddSingleton(options);

            // Registered only when installed, so injecting a RewriteHost is proof there is
            // one rather than a null that has to be checked at every use.
            var host = PerformanceSqlShim.Host;

            if (host != null)
            {
                services.AddSingleton(host);
            }

            return services;
        }
    }
}
