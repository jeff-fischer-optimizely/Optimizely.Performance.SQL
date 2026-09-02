using System;
using Optimizely.Performance.SQL.Configuration;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Everything the ADO.NET decorators need, bundled so the factory can hand a single
    /// reference down the connection/command chain.
    /// </summary>
    /// <remarks>
    /// <see cref="Registry"/> is volatile so a configuration reload can swap the whole
    /// decision engine atomically while commands are in flight. Readers get either the
    /// old registry or the new one, never a half-built one.
    /// </remarks>
    public sealed class RewriteContext
    {
        private volatile SqlRewriteRegistry _registry;

        public RewriteContext(
            SqlRewriteRegistry registry,
            RewriteOptions options,
            IDatabaseCapabilityProvider capabilityProvider = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Options = options ?? new RewriteOptions();
            CapabilityProvider = capabilityProvider ?? NullCapabilityProvider.Instance;
        }

        /// <summary>The current decision engine.</summary>
        public SqlRewriteRegistry Registry
        {
            get { return _registry; }
        }

        /// <summary>Host configuration.</summary>
        public RewriteOptions Options { get; }

        /// <summary>Source of probed database facts.</summary>
        public IDatabaseCapabilityProvider CapabilityProvider { get; }

        /// <summary>
        /// Atomically replaces the registry, for configuration reload.
        /// </summary>
        public void SwapRegistry(SqlRewriteRegistry registry)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            _registry = registry;
        }

        /// <summary>
        /// True when there is no chance of a rewrite, letting the command decorator skip
        /// fingerprinting altogether.
        /// </summary>
        public bool IsInert
        {
            get { return !Options.Enabled || _registry.IsEmpty; }
        }
    }
}
