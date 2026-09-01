using System.Collections.Generic;
using System.Data.Common;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Tests.Fakes
{
    /// <summary>Captures every decision the registry reports, in order.</summary>
    public sealed class RecordingObserver : IRewriteObserver
    {
        private readonly List<RewriteEvent> _events = new List<RewriteEvent>();

        public IReadOnlyList<RewriteEvent> Events
        {
            get { return _events; }
        }

        public RewriteEvent Last
        {
            get { return _events.Count == 0 ? null : _events[_events.Count - 1]; }
        }

        public void OnRewriteEvaluated(RewriteEvent evaluation)
        {
            _events.Add(evaluation);
        }
    }

    /// <summary>An observer that always throws, to prove diagnostics cannot break a query.</summary>
    public sealed class ThrowingObserver : IRewriteObserver
    {
        public void OnRewriteEvaluated(RewriteEvent evaluation)
        {
            throw new System.InvalidOperationException("observer is broken");
        }
    }

    /// <summary>Returns a fixed set of capabilities regardless of the connection.</summary>
    public sealed class StubCapabilityProvider : IDatabaseCapabilityProvider
    {
        private readonly DatabaseCapabilities _capabilities;

        public StubCapabilityProvider(DatabaseCapabilities capabilities)
        {
            _capabilities = capabilities;
        }

        public int CallCount { get; private set; }

        /// <summary>The transaction the decorator passed on the most recent call.</summary>
        public DbTransaction LastTransaction { get; private set; }

        public DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null)
        {
            CallCount++;
            LastTransaction = transaction;
            return _capabilities;
        }
    }

    /// <summary>A capability provider that throws, to prove the command decorator fails open.</summary>
    public sealed class ThrowingCapabilityProvider : IDatabaseCapabilityProvider
    {
        public DatabaseCapabilities GetCapabilities(DbConnection connection, DbTransaction transaction = null)
        {
            throw new System.InvalidOperationException("probe exploded");
        }
    }
}
