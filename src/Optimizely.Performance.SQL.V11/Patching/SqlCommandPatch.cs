using System;
using System.Data.Common;
using System.Threading;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.V11.Patching
{
    /// <summary>
    /// What one patched execution did, carried from the prefix to the finalizer.
    /// </summary>
    /// <remarks>
    /// Internal rather than nested and private because Harmony declares a local of this
    /// type in the IL it generates for the patched method.
    /// </remarks>
    internal struct CommandInterception
    {
        /// <summary>
        /// True when this frame is the one that claimed the command, and therefore the one
        /// responsible for releasing it. Only that frame restores.
        /// </summary>
        internal bool Owns;

        /// <summary>The substitution to undo, or <see cref="CommandRewrite.None"/>.</summary>
        internal CommandRewrite Rewrite;
    }

    /// <summary>
    /// The patch bodies applied to <c>System.Data.SqlClient.SqlCommand</c>'s execution
    /// methods: substitute approved SQL on the way in, put the command back on the way out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>finalizer</b> rather than a postfix does the restoring, because Harmony skips
    /// postfixes when the original throws and a failed query must not leave the command
    /// holding rewritten text. EPiServer retries on deadlock with the same command object,
    /// and a rewrite that dropped parameters would make that retry fail with a missing
    /// parameter rather than the transient error it was retrying.
    /// </para>
    /// <para>
    /// The finalizer costs exception fidelity: Harmony wraps the method in a try/catch and
    /// rethrows, which resets the stack trace. Measured against the in-box SqlClient, a
    /// <c>SqlException</c> came back with 3 frames instead of 10. The frames lost are
    /// SqlClient's own internals below the execution call; the exception type, message,
    /// number and the entire EPiServer call chain above are unaffected. That is the right
    /// trade: the lost frames never identify which query failed, and the alternative is a
    /// command left in a state its owner did not put it in.
    /// </para>
    /// </remarks>
    internal static class SqlCommandPatch
    {
        /// <summary>
        /// The command whose execution is already being intercepted on this thread.
        /// </summary>
        /// <remarks>
        /// SqlCommand's execution methods call each other, so one logical call enters the
        /// patched set more than once. Measured on net472: <c>ExecuteReader()</c> reaches
        /// <c>ExecuteReader(CommandBehavior)</c>, and <c>ExecuteDbDataReader</c> reaches it
        /// too, so every reader on the site would otherwise be rewritten twice — the second
        /// time fingerprinting text the shim itself had just produced.
        /// <para>
        /// Keyed by command instance rather than a bare flag so it is self-healing. If a
        /// finalizer is ever missed, the stale reference only suppresses interception for
        /// that one command; the next command on the thread claims the slot normally.
        /// </para>
        /// <para>
        /// Thread-static is sound for the async overloads too. The nesting and the
        /// finalizer both run synchronously, before the returned <see cref="System.Threading.Tasks.Task"/>
        /// leaves the outermost patched frame.
        /// </para>
        /// </remarks>
        [ThreadStatic]
        private static DbCommand _inFlight;

        private static volatile RewriteContext _context;
        private static long _applied;
        private static long _restored;
        private static long _restoreFailures;

        /// <summary>
        /// The context the patches resolve against. Null switches interception off without
        /// unpatching, which is how <see cref="PerformanceSqlShim.Uninstall"/> stops work
        /// reaching commands already in flight.
        /// </summary>
        internal static RewriteContext Context
        {
            get { return _context; }
            set { _context = value; }
        }

        /// <summary>Number of commands whose text has been substituted.</summary>
        internal static long Applied
        {
            get { return Interlocked.Read(ref _applied); }
        }

        /// <summary>Number of substitutions undone. Should track <see cref="Applied"/> exactly.</summary>
        internal static long Restored
        {
            get { return Interlocked.Read(ref _restored); }
        }

        /// <summary>Number of times restoring threw. Any non-zero value is a defect.</summary>
        internal static long RestoreFailures
        {
            get { return Interlocked.Read(ref _restoreFailures); }
        }

        internal static void ResetCounters()
        {
            Interlocked.Exchange(ref _applied, 0);
            Interlocked.Exchange(ref _restored, 0);
            Interlocked.Exchange(ref _restoreFailures, 0);
        }

        /// <summary>
        /// Runs before the original. Claims the command and applies the substitution.
        /// </summary>
        /// <remarks>
        /// Sits in front of every query the CMS issues, so it cannot throw: an exception
        /// here surfaces at the call site as though the database had failed.
        /// </remarks>
        internal static void Prefix(DbCommand __instance, ref CommandInterception __state)
        {
            __state = default(CommandInterception);

            try
            {
                var context = _context;

                if (context == null || __instance == null || ReferenceEquals(_inFlight, __instance))
                {
                    return;
                }

                // Claim before rewriting, so the finalizer releases even if the rewrite
                // itself goes wrong.
                _inFlight = __instance;
                __state.Owns = true;

                __state.Rewrite = CommandRewriter.Apply(__instance, context);

                if (__state.Rewrite.Applied)
                {
                    Interlocked.Increment(ref _applied);
                }
            }
            catch (Exception)
            {
                // Fail open. An unrewritten query is slow; a throwing shim is an outage.
            }
        }

        /// <summary>
        /// Runs after the original whether it returned or threw. Undoes the substitution.
        /// </summary>
        /// <returns>
        /// <paramref name="__exception"/> unchanged. Returning anything else would alter
        /// what the caller sees, and this patch has no business doing that.
        /// </returns>
        internal static Exception Finalizer(ref CommandInterception __state, Exception __exception)
        {
            if (!__state.Owns)
            {
                return __exception;
            }

            _inFlight = null;
            __state.Owns = false;

            try
            {
                if (__state.Rewrite.Applied)
                {
                    __state.Rewrite.Restore();
                    Interlocked.Increment(ref _restored);
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _restoreFailures);
            }

            return __exception;
        }
    }
}
