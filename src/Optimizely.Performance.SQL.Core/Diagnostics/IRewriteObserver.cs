using System;

namespace Optimizely.Performance.SQL.Diagnostics
{
    /// <summary>
    /// Receives notifications about rewrite decisions. Implementations are called on the
    /// data path, so they must be fast, non-throwing and non-blocking; the shim guards
    /// against exceptions but not against latency.
    /// </summary>
    public interface IRewriteObserver
    {
        /// <summary>
        /// Raised for every statement the registry matched to an approved entry,
        /// whether or not it was actually substituted. Unmatched statements are not
        /// reported, since that is the normal case for nearly all traffic.
        /// </summary>
        void OnRewriteEvaluated(RewriteEvent evaluation);
    }

    /// <summary>
    /// Details of one rewrite decision.
    /// </summary>
    public sealed class RewriteEvent
    {
        public RewriteEvent(
            string statementId,
            Rewriting.RewriteOutcome outcome,
            string fingerprint,
            string originalSql,
            string rewrittenSql)
        {
            StatementId = statementId;
            Outcome = outcome;
            Fingerprint = fingerprint;
            OriginalSql = originalSql;
            RewrittenSql = rewrittenSql;
        }

        /// <summary>Entry id, with variant suffix when one was selected.</summary>
        public string StatementId { get; }

        /// <summary>What the registry decided.</summary>
        public Rewriting.RewriteOutcome Outcome { get; }

        /// <summary>Fingerprint of the incoming statement.</summary>
        public string Fingerprint { get; }

        /// <summary>Statement as the CMS emitted it.</summary>
        public string OriginalSql { get; }

        /// <summary>Replacement text, or null when nothing was substituted.</summary>
        public string RewrittenSql { get; }
    }

    /// <summary>
    /// Fans out to several observers and swallows their failures. A broken observer must
    /// never fail a customer's query.
    /// </summary>
    public sealed class CompositeRewriteObserver : IRewriteObserver
    {
        private readonly IRewriteObserver[] _observers;

        public CompositeRewriteObserver(params IRewriteObserver[] observers)
        {
            _observers = observers ?? Array.Empty<IRewriteObserver>();
        }

        public void OnRewriteEvaluated(RewriteEvent evaluation)
        {
            foreach (var observer in _observers)
            {
                try
                {
                    observer.OnRewriteEvaluated(evaluation);
                }
                catch
                {
                    // Diagnostics are strictly best-effort.
                }
            }
        }
    }
}
