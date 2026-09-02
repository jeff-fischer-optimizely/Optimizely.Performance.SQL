using System;
using System.Collections.Generic;
using System.Data.Common;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Interception
{
    /// <summary>
    /// A substitution that has been applied to a command in place, together with the
    /// means to undo it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The command is mutated rather than wrapped. That is not a stylistic choice: every
    /// Optimizely CMS version casts the command it gets back to the concrete provider
    /// type in dozens of places (25 sites in CMS 11, 24 in CMS 12), and
    /// <c>SqlCommand</c> is sealed, so a decorator can never survive those casts. Editing
    /// the real object and putting it back is the only interception shape that works.
    /// </para>
    /// <para>
    /// Restoring matters because EPiServer reuses command objects and reads
    /// <c>CommandText</c> back for its own logging. A caller that never looks will not
    /// notice either way, but one that does must see what it set.
    /// </para>
    /// </remarks>
    public readonly struct CommandRewrite : IDisposable
    {
        /// <summary>The "nothing was changed" value. Restoring it is a no-op.</summary>
        public static readonly CommandRewrite None = default;

        private readonly DbCommand _command;
        private readonly string _originalText;
        private readonly List<KeyValuePair<int, DbParameter>> _removed;

        internal CommandRewrite(
            DbCommand command,
            string originalText,
            List<KeyValuePair<int, DbParameter>> removed,
            RewriteResult result)
        {
            _command = command;
            _originalText = originalText;
            _removed = removed;
            Result = result;
        }

        /// <summary>True when the command was actually modified.</summary>
        public bool Applied
        {
            get { return _command != null; }
        }

        /// <summary>
        /// What the registry decided. Null when the statement was never looked up, which
        /// is different from "looked up and left alone".
        /// </summary>
        public RewriteResult Result { get; }

        /// <summary>The text the caller originally set, or null when nothing was changed.</summary>
        public string OriginalText
        {
            get { return _originalText; }
        }

        /// <summary>
        /// Puts the command back exactly as the caller configured it. Safe to call on
        /// <see cref="None"/> and safe to call more than once.
        /// </summary>
        public void Restore()
        {
            if (_command == null)
            {
                return;
            }

            _command.CommandText = _originalText;

            if (_removed == null)
            {
                return;
            }

            // Re-insert descending through the recorded list so every parameter lands
            // back at the index it came from.
            for (var i = _removed.Count - 1; i >= 0; i--)
            {
                var entry = _removed[i];
                var index = Math.Min(entry.Key, _command.Parameters.Count);
                _command.Parameters.Insert(index, entry.Value);
            }
        }

        /// <summary>Calls <see cref="Restore"/>, so the rewrite can be scoped with <c>using</c>.</summary>
        public void Dispose()
        {
            Restore();
        }
    }
}
