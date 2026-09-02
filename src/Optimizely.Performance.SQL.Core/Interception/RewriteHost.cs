using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Interception
{
    /// <summary>
    /// Builds and owns the <see cref="RewriteContext"/>: loads the approved-SQL document,
    /// wires the capability probe to the procedures that document actually references, and
    /// keeps both current when configuration changes on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the composition root shared by the CMS 11 and CMS 12 adapters. Neither
    /// hosting model gets to assemble the pieces itself, because the pieces have to agree:
    /// the capability provider is constructed from the registry's procedure list, so a
    /// registry swap that left the old provider in place would check a redirect's
    /// preconditions against module hashes that were never fetched.
    /// </para>
    /// <para>
    /// Failure discipline differs between the first load and later ones. A first load that
    /// fails leaves the host inert, which is the correct fail-open: a site that has not
    /// approved anything yet must still start. A <em>reload</em> that fails keeps the
    /// configuration already in force, because the overwhelmingly likely cause is reading
    /// the file halfway through an operator's save, and going inert on a half-written file
    /// would silently switch the optimisation off in production.
    /// </para>
    /// </remarks>
    public sealed class RewriteHost : IDisposable
    {
        private readonly IRewriteObserver _observer;
        private readonly string _baseDirectory;
        private readonly SwappableCapabilityProvider _capabilities;
        private readonly object _reloadGate = new object();
        private FileSystemWatcher _watcher;
        private int _disposed;

        private RewriteHost(RewriteOptions options, IRewriteObserver observer, string baseDirectory)
        {
            Options = options ?? new RewriteOptions();
            _observer = observer;
            _baseDirectory = baseDirectory;
            _capabilities = new SwappableCapabilityProvider();

            ConfigurationPath = ApprovedSqlLoader.Resolve(Options.ConfigurationPath, baseDirectory);

            Context = new RewriteContext(
                new SqlRewriteRegistry(ApprovedSqlLoader.Empty, Options, _observer),
                Options,
                _capabilities);
        }

        /// <summary>
        /// Loads configuration and returns a host ready to hand to an adapter.
        /// </summary>
        /// <param name="options">Host configuration. Defaults are used when null.</param>
        /// <param name="observer">Optional sink for per-statement rewrite decisions.</param>
        /// <param name="baseDirectory">
        /// Root for a relative <see cref="RewriteOptions.ConfigurationPath"/>. Defaults to
        /// the application base directory, which is what both CMS hosts want.
        /// </param>
        public static RewriteHost Create(
            RewriteOptions options = null,
            IRewriteObserver observer = null,
            string baseDirectory = null)
        {
            var host = new RewriteHost(options, observer, baseDirectory);

            host.Load(initial: true);
            host.StartWatching();

            return host;
        }

        /// <summary>The context to give the adapter. The same instance for the host's life.</summary>
        public RewriteContext Context { get; }

        /// <summary>Host configuration, as supplied.</summary>
        public RewriteOptions Options { get; }

        /// <summary>Absolute path the configuration was read from, or null if none is configured.</summary>
        public string ConfigurationPath { get; }

        /// <summary>Why the last load attempt failed, or null if it succeeded.</summary>
        public string LoadError { get; private set; }

        /// <summary>Number of approved statements currently in force.</summary>
        public int StatementCount { get; private set; }

        /// <summary>
        /// Re-reads configuration and, on success, swaps it in atomically.
        /// </summary>
        /// <returns>True if the new configuration was applied.</returns>
        public bool Reload()
        {
            return Load(initial: false);
        }

        private bool Load(bool initial)
        {
            lock (_reloadGate)
            {
                ApprovedSqlDocument document;
                string error;

                if (!ApprovedSqlLoader.TryLoad(Options.ConfigurationPath, out document, out error, _baseDirectory))
                {
                    LoadError = error;

                    // Keep what is already running. Only the very first load is allowed to
                    // leave the host inert.
                    return initial;
                }

                var registry = new SqlRewriteRegistry(document, Options, _observer);

                // Provider first: a command that has already picked up the new registry must
                // not be able to check its preconditions against the old probe results.
                _capabilities.Swap(
                    Options.ProbeDatabaseCapabilities
                        ? (IDatabaseCapabilityProvider)new SqlServerCapabilityProvider(registry.ProcedureNames)
                        : NullCapabilityProvider.Instance);

                Context.SwapRegistry(registry);

                StatementCount = document.Statements == null ? 0 : document.Statements.Length;
                LoadError = null;

                return true;
            }
        }

        private void StartWatching()
        {
            if (!Options.ReloadOnChange || ConfigurationPath == null)
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(ConfigurationPath);

                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                var watcher = new FileSystemWatcher(directory, Path.GetFileName(ConfigurationPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };

                // Renamed as well as Changed: most editors and deployment tools write to a
                // temporary file and move it into place, which never raises Changed.
                watcher.Changed += OnConfigurationChanged;
                watcher.Created += OnConfigurationChanged;
                watcher.Renamed += OnConfigurationChanged;
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
            }
            catch (Exception)
            {
                // Watching is a convenience. A host that cannot watch still rewrites.
            }
        }

        private void OnConfigurationChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                Reload();
            }
            catch (Exception)
            {
                // Never let a background file event escape onto the watcher thread.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var watcher = _watcher;
            _watcher = null;

            if (watcher == null)
            {
                return;
            }

            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnConfigurationChanged;
                watcher.Created -= OnConfigurationChanged;
                watcher.Renamed -= OnConfigurationChanged;
                watcher.Dispose();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Indirection that lets a reload replace the capability provider without replacing
        /// the <see cref="RewriteContext"/> the adapters are already holding.
        /// </summary>
        /// <remarks>
        /// Replacing rather than reusing is deliberate: the provider caches probe results
        /// for the process lifetime, and a reload is exactly the moment those results may
        /// have gone stale.
        /// </remarks>
        private sealed class SwappableCapabilityProvider : IDatabaseCapabilityProvider
        {
            private volatile IDatabaseCapabilityProvider _inner = NullCapabilityProvider.Instance;

            public void Swap(IDatabaseCapabilityProvider inner)
            {
                _inner = inner ?? NullCapabilityProvider.Instance;
            }

            public DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null)
            {
                return _inner.GetCapabilities(connection, transaction);
            }
        }
    }
}
