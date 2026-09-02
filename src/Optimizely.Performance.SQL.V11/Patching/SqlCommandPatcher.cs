using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Reflection;
using HarmonyLib;

namespace Optimizely.Performance.SQL.V11.Patching
{
    /// <summary>
    /// Finds and patches the execution methods on <see cref="SqlCommand"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is referenced directly. A CMS 11 site may have the
    /// <c>System.Data.SqlClient</c> NuGet package installed, but on net461 and above that
    /// package is a type-forwarding facade onto <c>System.Data.dll</c>, so both routes
    /// arrive at the same runtime type and one patch set covers both.
    /// </para>
    /// <para>
    /// Targets are selected by name and filtered to methods <see cref="SqlCommand"/> itself
    /// declares, which drops the inherited <c>DbCommand</c> members Harmony could not patch
    /// usefully anyway. Everything matching is patched, including the overloads that only
    /// SqlClient's internals call. Trimming the set to the funnel each overload happens to
    /// route through today would be smaller but would silently lose coverage the moment a
    /// .NET Framework servicing update reshuffles those internals; the reentrancy guard in
    /// <see cref="SqlCommandPatch"/> already makes the redundancy free.
    /// </para>
    /// </remarks>
    internal static class SqlCommandPatcher
    {
        /// <summary>
        /// The execution entry points.
        /// </summary>
        /// <remarks>
        /// <c>ExecuteDbDataReader</c> and <c>ExecuteDbDataReaderAsync</c> are protected and
        /// are not optional. <see cref="System.Data.Common.DbCommand.ExecuteReader()"/> is
        /// not virtual; it dispatches to the protected override. CMS 11 holds the command
        /// as a <c>DbCommand</c> at roughly 97 call sites, so leaving these two out patches
        /// the site and rewrites nothing on exactly the code paths that matter.
        /// </remarks>
        private static readonly string[] TargetNames =
        {
            "ExecuteReader",
            "ExecuteNonQuery",
            "ExecuteScalar",
            "ExecuteXmlReader",
            "ExecuteReaderAsync",
            "ExecuteNonQueryAsync",
            "ExecuteScalarAsync",
            "ExecuteXmlReaderAsync",
            "ExecuteDbDataReader",
            "ExecuteDbDataReaderAsync"
        };

        /// <summary>Every method the patcher intends to hook, in declaration order.</summary>
        internal static IEnumerable<MethodInfo> FindTargets()
        {
            var names = new HashSet<string>(TargetNames, StringComparer.Ordinal);

            var candidates = typeof(SqlCommand).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var method in candidates)
            {
                if (method.DeclaringType != typeof(SqlCommand)
                    || method.IsAbstract
                    || !names.Contains(method.Name))
                {
                    continue;
                }

                yield return method;
            }
        }

        /// <summary>
        /// Applies the prefix and finalizer to every target, reporting per-method failures
        /// rather than aborting.
        /// </summary>
        /// <remarks>
        /// Partial success is a real outcome worth keeping. A runtime that refuses one
        /// overload still lets the shim rewrite everything reaching the others, and the
        /// report says exactly what was missed.
        /// </remarks>
        internal static PatchReport Apply(Harmony harmony)
        {
            var prefix = new HarmonyMethod(Method(nameof(SqlCommandPatch.Prefix)));
            var finalizer = new HarmonyMethod(Method(nameof(SqlCommandPatch.Finalizer)));

            var patched = new List<string>();
            var failures = new List<string>();

            foreach (var method in FindTargets())
            {
                var signature = Describe(method);

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: null, transpiler: null, finalizer: finalizer);
                    patched.Add(signature);
                }
                catch (Exception ex)
                {
                    failures.Add(signature + ": " + FirstLine(ex.Message));
                }
            }

            return new PatchReport(patched.ToArray(), failures.ToArray());
        }

        /// <summary>Removes every patch this instance applied.</summary>
        internal static void Remove(Harmony harmony)
        {
            harmony.UnpatchAll(harmony.Id);
        }

        private static MethodInfo Method(string name)
        {
            var method = typeof(SqlCommandPatch).GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new MissingMethodException(typeof(SqlCommandPatch).FullName, name);
            }

            return method;
        }

        private static string Describe(MethodBase method)
        {
            var parameters = method.GetParameters();
            var types = new string[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                types[i] = parameters[i].ParameterType.Name;
            }

            return method.Name + "(" + string.Join(", ", types) + ")";
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var end = text.IndexOfAny(new[] { '\r', '\n' });

            return end < 0 ? text : text.Substring(0, end);
        }
    }

    /// <summary>What patching achieved, and what it did not.</summary>
    public sealed class PatchReport
    {
        internal PatchReport(string[] patchedMethods, string[] failures)
        {
            PatchedMethods = patchedMethods ?? Array.Empty<string>();
            Failures = failures ?? Array.Empty<string>();
        }

        /// <summary>Signatures of the methods now under interception.</summary>
        public string[] PatchedMethods { get; }

        /// <summary>Signatures the runtime refused, each with the reason.</summary>
        public string[] Failures { get; }

        /// <summary>True when at least one execution method is intercepted.</summary>
        public bool AnyPatched
        {
            get { return PatchedMethods.Length != 0; }
        }
    }
}
