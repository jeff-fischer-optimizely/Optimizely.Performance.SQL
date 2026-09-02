using System;
using System.Diagnostics;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.V12.Diagnostics;

namespace Optimizely.Performance.SQL.V12
{
    /// <summary>
    /// Installs approved-SQL rewriting into a CMS 12 site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CMS 12 runs on <c>Microsoft.Data.SqlClient</c>, which publishes a diagnostic source
    /// that announces every command immediately before it executes and hands over the
    /// command object itself. That is a supported, documented extension point, so unlike
    /// CMS 11 this adapter patches nothing.
    /// </para>
    /// <para>
    /// Call this as early as possible — the first statement of <c>Program.cs</c> is right —
    /// because commands issued before the subscription exists are simply not seen, and an
    /// Optimizely site does a great deal of its slowest database work while starting up.
    /// <see cref="Microsoft.Extensions.DependencyInjection.PerformanceSqlServiceCollectionExtensions.AddOptimizelyPerformanceSql(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{RewriteOptions}, IRewriteObserver)"/>
    /// installs eagerly for the same reason.
    /// </para>
    /// </remarks>
    public static class PerformanceSqlShim
    {
        private static readonly object Gate = new object();
        private static SqlClientDiagnosticSubscriber _subscriber;
        private static IDisposable _allListeners;
        private static RewriteHost _host;

        /// <summary>Outcome of the last install attempt.</summary>
        public static ShimStatus Status { get; private set; } = ShimStatus.NotInstalled;

        /// <summary>True when the diagnostic subscription is active.</summary>
        public static bool IsInstalled
        {
            get { return Status.Installed; }
        }

        /// <summary>The configuration host, or null when not installed.</summary>
        public static RewriteHost Host
        {
            get { return _host; }
        }

        /// <summary>Number of commands whose text has been substituted.</summary>
        public static long Applied
        {
            get
            {
                var subscriber = _subscriber;
                return subscriber == null ? 0 : subscriber.Applied;
            }
        }

        /// <summary>Number of substitutions undone. Should track <see cref="Applied"/> exactly.</summary>
        public static long Restored
        {
            get
            {
                var subscriber = _subscriber;
                return subscriber == null ? 0 : subscriber.Restored;
            }
        }

        /// <summary>
        /// Rewrites still awaiting their completion event. Near zero in steady state; a
        /// number that climbs means commands are being left rewritten.
        /// </summary>
        public static int InFlight
        {
            get
            {
                var subscriber = _subscriber;
                return subscriber == null ? 0 : subscriber.InFlight;
            }
        }

        /// <summary>
        /// Number of SqlClient diagnostic listeners subscribed to. Zero after a successful
        /// install just means SqlClient has not been touched yet.
        /// </summary>
        public static int ListenerCount
        {
            get
            {
                var subscriber = _subscriber;
                return subscriber == null ? 0 : subscriber.ListenerCount;
            }
        }

        /// <summary>
        /// Loads configuration and subscribes to the SqlClient diagnostic source.
        /// </summary>
        /// <param name="options">Host configuration. Defaults are used when null.</param>
        /// <param name="observer">Optional sink for per-statement rewrite decisions.</param>
        /// <param name="baseDirectory">
        /// Root for a relative configuration path. Defaults to the application base
        /// directory.
        /// </param>
        /// <remarks>
        /// Idempotent: calling it again on an installed shim reloads configuration and
        /// returns the existing status rather than subscribing twice.
        /// </remarks>
        public static ShimStatus Install(
            RewriteOptions options = null,
            IRewriteObserver observer = null,
            string baseDirectory = null)
        {
            lock (Gate)
            {
                if (_subscriber != null)
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

                if ((effective.Version & CmsVersion.V12) == 0)
                {
                    return Status = ShimStatus.Skipped("configuration does not target CMS 12");
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

                try
                {
                    var subscriber = new SqlClientDiagnosticSubscriber(host.Context);

                    _allListeners = System.Diagnostics.DiagnosticListener.AllListeners.Subscribe(subscriber);
                    _subscriber = subscriber;
                    _host = host;
                }
                catch (Exception ex)
                {
                    host.Dispose();

                    return Status = ShimStatus.Failed("could not subscribe to the SqlClient diagnostic source: " + ex.Message);
                }

                Status = ShimStatus.Success(host);

                Trace.WriteLine("[Optimizely.Performance.SQL] " + Status);

                return Status;
            }
        }

        /// <summary>
        /// Unsubscribes and restores anything still rewritten.
        /// </summary>
        public static void Uninstall()
        {
            lock (Gate)
            {
                var allListeners = _allListeners;
                _allListeners = null;

                if (allListeners != null)
                {
                    try
                    {
                        allListeners.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                var subscriber = _subscriber;
                _subscriber = null;

                if (subscriber != null)
                {
                    subscriber.Dispose();
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
    }
}
