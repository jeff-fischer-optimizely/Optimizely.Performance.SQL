using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Optimizely.Performance.SQL.Interception;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.V12.Diagnostics
{
    /// <summary>
    /// Substitutes approved SQL by listening to <c>Microsoft.Data.SqlClient</c>'s diagnostic
    /// source: rewrite the command when the provider announces it is about to run, put it
    /// back when the provider announces it has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is interception without wrapping, which is the only kind CMS 12 can use.
    /// EPiServer casts the command it gets back to <c>SqlCommand</c> at 24 sites and
    /// <c>SqlCommand</c> is sealed, so no decorator can survive. Here the provider hands us
    /// the real command and we edit it in place, so there is nothing to cast through.
    /// </para>
    /// <para>
    /// The subscription is deliberately narrow. Passing a predicate to
    /// <see cref="DiagnosticListener.Subscribe(IObserver{KeyValuePair{string, object}}, Predicate{string})"/>
    /// keeps SqlClient from building payload objects for the connection and transaction
    /// events nobody here reads.
    /// </para>
    /// </remarks>
    internal sealed class SqlClientDiagnosticSubscriber : IObserver<DiagnosticListener>, IDisposable
    {
        internal const string ListenerName = "SqlClientDiagnosticListener";
        internal const string CommandBefore = "Microsoft.Data.SqlClient.WriteCommandBefore";
        internal const string CommandAfter = "Microsoft.Data.SqlClient.WriteCommandAfter";
        internal const string CommandError = "Microsoft.Data.SqlClient.WriteCommandError";

        private readonly RewriteContext _context;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();
        private readonly object _gate = new object();

        /// <summary>
        /// Rewrites awaiting their matching completion event, keyed by the provider's
        /// operation id.
        /// </summary>
        /// <remarks>
        /// A map rather than thread-local state because the before and after events for one
        /// async execution need not run on the same thread. SqlClient raises the completion
        /// event from a <c>finally</c>, so entries do not survive the operation.
        /// </remarks>
        private readonly ConcurrentDictionary<Guid, CommandRewrite> _inFlight =
            new ConcurrentDictionary<Guid, CommandRewrite>();

        /// <summary>
        /// Property lookups cached per payload type. The payloads are compiler-generated
        /// anonymous types, so there is a small fixed set of them and reflecting once each
        /// keeps the per-command cost to two property reads.
        /// </summary>
        private readonly ConcurrentDictionary<Type, PayloadAccessor> _accessors =
            new ConcurrentDictionary<Type, PayloadAccessor>();

        private long _applied;
        private long _restored;
        private int _disposed;

        internal SqlClientDiagnosticSubscriber(RewriteContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>Number of commands whose text has been substituted.</summary>
        internal long Applied
        {
            get { return Interlocked.Read(ref _applied); }
        }

        /// <summary>Number of substitutions undone. Should track <see cref="Applied"/> exactly.</summary>
        internal long Restored
        {
            get { return Interlocked.Read(ref _restored); }
        }

        /// <summary>
        /// Rewrites still waiting for a completion event. Steady-state this sits near zero;
        /// a number that climbs means completion events are being missed.
        /// </summary>
        internal int InFlight
        {
            get { return _inFlight.Count; }
        }

        /// <summary>Number of <c>SqlClientDiagnosticListener</c> instances subscribed to.</summary>
        internal int ListenerCount
        {
            get
            {
                lock (_gate)
                {
                    return _subscriptions.Count;
                }
            }
        }

        void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
        {
            if (listener == null || listener.Name != ListenerName)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed != 0)
                {
                    return;
                }

                // More than one listener of this name can exist — a process that has loaded
                // SqlClient into several contexts gets one each — and a command is announced
                // on exactly one of them, so subscribe to all and let the keys sort it out.
                _subscriptions.Add(listener.Subscribe(new CommandEvents(this), IsCommandEvent));
            }
        }

        void IObserver<DiagnosticListener>.OnCompleted()
        {
        }

        void IObserver<DiagnosticListener>.OnError(Exception error)
        {
        }

        private static bool IsCommandEvent(string name)
        {
            return name == CommandBefore || name == CommandAfter || name == CommandError;
        }

        private void Handle(KeyValuePair<string, object> notification)
        {
            var payload = notification.Value;

            if (payload == null)
            {
                return;
            }

            var accessor = _accessors.GetOrAdd(payload.GetType(), PayloadAccessor.For);

            if (!accessor.Usable)
            {
                return;
            }

            if (notification.Key == CommandBefore)
            {
                Begin(accessor, payload);
                return;
            }

            End(accessor, payload);
        }

        private void Begin(PayloadAccessor accessor, object payload)
        {
            var command = accessor.ReadCommand(payload);

            if (command == null)
            {
                return;
            }

            var rewrite = CommandRewriter.Apply(command, _context);

            if (!rewrite.Applied)
            {
                return;
            }

            Guid operationId;

            if (!accessor.TryReadOperationId(payload, out operationId))
            {
                // No key to match the completion event against, so nothing could ever undo
                // this. Undo it now and leave the statement alone.
                rewrite.Restore();
                return;
            }

            _inFlight[operationId] = rewrite;
            Interlocked.Increment(ref _applied);
        }

        private void End(PayloadAccessor accessor, object payload)
        {
            Guid operationId;

            if (!accessor.TryReadOperationId(payload, out operationId))
            {
                return;
            }

            CommandRewrite rewrite;

            if (!_inFlight.TryRemove(operationId, out rewrite))
            {
                return;
            }

            rewrite.Restore();
            Interlocked.Increment(ref _restored);
        }

        public void Dispose()
        {
            List<IDisposable> subscriptions;

            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                subscriptions = new List<IDisposable>(_subscriptions);
                _subscriptions.Clear();
            }

            foreach (var subscription in subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception)
                {
                }
            }

            // Anything still in flight will never see its completion event now.
            foreach (var entry in _inFlight)
            {
                CommandRewrite rewrite;

                if (_inFlight.TryRemove(entry.Key, out rewrite))
                {
                    try
                    {
                        rewrite.Restore();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Receives the command events for one listener and hands them back to the owner.
        /// </summary>
        /// <remarks>
        /// Separate from the outer class because that one is already the
        /// <c>IObserver&lt;DiagnosticListener&gt;</c>, and the two streams carry different
        /// payloads.
        /// </remarks>
        private sealed class CommandEvents : IObserver<KeyValuePair<string, object>>
        {
            private readonly SqlClientDiagnosticSubscriber _owner;

            internal CommandEvents(SqlClientDiagnosticSubscriber owner)
            {
                _owner = owner;
            }

            public void OnNext(KeyValuePair<string, object> value)
            {
                try
                {
                    _owner.Handle(value);
                }
                catch (Exception)
                {
                    // This runs inside SqlClient's execution path. Throwing here would
                    // surface at the call site as a database failure.
                }
            }

            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }
        }

        /// <summary>
        /// Reads <c>Command</c> and <c>OperationId</c> off one shape of diagnostic payload.
        /// </summary>
        /// <remarks>
        /// By reflection, and by design: the payload's <c>Command</c> is typed as
        /// <c>Microsoft.Data.SqlClient.SqlCommand</c>, and referencing that assembly would
        /// bind this adapter to one SqlClient version out of the several a CMS 12 site might
        /// resolve. Read as <see cref="DbCommand"/>, every version works.
        /// </remarks>
        private sealed class PayloadAccessor
        {
            private readonly PropertyInfo _command;
            private readonly PropertyInfo _operationId;

            private PayloadAccessor(PropertyInfo command, PropertyInfo operationId)
            {
                _command = command;
                _operationId = operationId;
            }

            internal bool Usable
            {
                get { return _command != null && _operationId != null; }
            }

            internal static PayloadAccessor For(Type payloadType)
            {
                const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

                return new PayloadAccessor(
                    payloadType.GetProperty("Command", Flags),
                    payloadType.GetProperty("OperationId", Flags));
            }

            internal DbCommand ReadCommand(object payload)
            {
                return _command.GetValue(payload) as DbCommand;
            }

            internal bool TryReadOperationId(object payload, out Guid operationId)
            {
                var value = _operationId.GetValue(payload);

                if (value is Guid guid)
                {
                    operationId = guid;
                    return true;
                }

                operationId = Guid.Empty;
                return false;
            }
        }
    }
}
