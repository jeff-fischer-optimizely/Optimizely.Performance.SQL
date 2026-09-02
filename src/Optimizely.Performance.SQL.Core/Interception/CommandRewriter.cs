using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Interception
{
    /// <summary>
    /// Resolves a command against the approved-SQL registry and applies the substitution
    /// in place. This is the whole interception behaviour; the hosting adapters only
    /// decide where to call it from.
    /// </summary>
    /// <remarks>
    /// Every adapter funnels through here so CMS 11 (Harmony patches on
    /// <c>System.Data.SqlClient</c>) and CMS 12 (a <c>Microsoft.Data.SqlClient</c>
    /// diagnostic subscription) cannot drift apart in what they consider safe.
    /// </remarks>
    public static class CommandRewriter
    {
        /// <summary>
        /// Guards against the capability probe re-entering the interceptor.
        /// </summary>
        /// <remarks>
        /// This is load-bearing, and it is new to the patching design. The old decorator
        /// probed through the *inner* connection, so the probe's own commands were never
        /// wrapped and could not recurse. A patched <c>SqlCommand</c> has no inner object
        /// to hide behind: the probe executes real commands, those commands hit the same
        /// prefix, and that prefix would probe again. Without this flag the first
        /// statement of the process recurses until the stack runs out.
        /// <para>
        /// Thread-static rather than async-local because the guard only has to span
        /// <see cref="Apply"/>, and capability probing is synchronous throughout.
        /// </para>
        /// </remarks>
        [ThreadStatic]
        private static bool _resolving;

        /// <summary>
        /// Looks up <paramref name="command"/> and, if an approved replacement applies,
        /// swaps its text in place.
        /// </summary>
        /// <returns>
        /// A handle that undoes the change. <see cref="CommandRewrite.None"/> when
        /// nothing was substituted, which is the overwhelmingly common answer.
        /// </returns>
        /// <remarks>
        /// Never throws. A shim sitting in front of every query in the CMS has exactly
        /// one acceptable failure mode: leave the statement alone and let it run.
        /// </remarks>
        public static CommandRewrite Apply(DbCommand command, RewriteContext context)
        {
            if (command == null || context == null || _resolving)
            {
                return CommandRewrite.None;
            }

            var commandType = command.CommandType;

            if (context.IsInert
                || (commandType != CommandType.Text && commandType != CommandType.StoredProcedure))
            {
                return CommandRewrite.None;
            }

            // Procedure redirects are rare and configured per deployment. Skip the probe
            // entirely when none are loaded, so a sproc-heavy stack such as CMS 11 pays
            // nothing for the feature merely being present.
            if (commandType == CommandType.StoredProcedure && !context.Registry.HasProcedureRedirects)
            {
                return CommandRewrite.None;
            }

            _resolving = true;

            try
            {
                // The transaction goes with the connection: a probe issued outside the
                // caller's pending transaction is rejected by the provider.
                var capabilities = context.Options.ProbeDatabaseCapabilities
                    ? context.CapabilityProvider.GetCapabilities(command.Connection, command.Transaction)
                    : DatabaseCapabilities.Unknown;

                var result = commandType == CommandType.StoredProcedure
                    ? context.Registry.ResolveProcedure(command.CommandText, capabilities)
                    : context.Registry.Resolve(command.CommandText, command.Parameters, capabilities);

                if (!result.ShouldReplace)
                {
                    return CommandRewrite.None;
                }

                var originalText = command.CommandText;
                var removed = RemoveDroppedParameters(command, result.DroppedParameters);

                command.CommandText = result.Sql;

                return new CommandRewrite(command, originalText, removed, result);
            }
            catch
            {
                // Fail open: an unrewritten query is slow, a failed query is an outage.
                return CommandRewrite.None;
            }
            finally
            {
                _resolving = false;
            }
        }

        /// <summary>
        /// Strips parameters the replacement no longer references, keeping the
        /// <c>sp_executesql</c> signature aligned with the batch actually being run.
        /// </summary>
        private static List<KeyValuePair<int, DbParameter>> RemoveDroppedParameters(
            DbCommand command,
            string[] dropped)
        {
            if (dropped == null || dropped.Length == 0 || command.Parameters.Count == 0)
            {
                return null;
            }

            List<KeyValuePair<int, DbParameter>> removed = null;

            foreach (var name in dropped)
            {
                for (var i = command.Parameters.Count - 1; i >= 0; i--)
                {
                    var parameter = command.Parameters[i];

                    if (parameter == null || !NameMatches(parameter.ParameterName, name))
                    {
                        continue;
                    }

                    removed ??= new List<KeyValuePair<int, DbParameter>>();
                    removed.Add(new KeyValuePair<int, DbParameter>(i, parameter));
                    command.Parameters.RemoveAt(i);
                }
            }

            return removed;
        }

        private static bool NameMatches(string actual, string expected)
        {
            return string.Equals(Strip(actual), Strip(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static string Strip(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var first = name[0];
            return first == '@' || first == ':' || first == '?' ? name.Substring(1) : name;
        }
    }
}
