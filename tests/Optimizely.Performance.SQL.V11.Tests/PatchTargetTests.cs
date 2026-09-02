using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using Optimizely.Performance.SQL.V11.Patching;
using Xunit;

namespace Optimizely.Performance.SQL.V11.Tests
{
    /// <summary>
    /// Which methods get patched, asserted without touching a database.
    /// </summary>
    /// <remarks>
    /// This set is the part of the adapter most likely to rot silently. Getting it wrong
    /// does not throw, does not log and does not fail a smoke test — the site simply runs
    /// unrewritten on whichever call shape was missed.
    /// </remarks>
    public sealed class PatchTargetTests
    {
        private static readonly HashSet<string> Targets =
            new HashSet<string>(SqlCommandPatcher.FindTargets().Select(Signature));

        private static string Signature(System.Reflection.MethodInfo method)
        {
            return method.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name)) + ")";
        }

        /// <summary>
        /// The regression guard for the coverage gap that cost the most to find.
        /// </summary>
        /// <remarks>
        /// <c>DbCommand.ExecuteReader()</c> is not virtual; it dispatches to the protected
        /// <c>ExecuteDbDataReader</c>. CMS 11 holds the command as a <c>DbCommand</c> at
        /// roughly 97 call sites, so patching only the public methods leaves exactly the
        /// code paths the shim exists for running the original SQL.
        /// </remarks>
        [Fact]
        public void Protected_reader_overrides_are_patched()
        {
            Assert.Contains("ExecuteDbDataReader(CommandBehavior)", Targets);
            Assert.Contains("ExecuteDbDataReaderAsync(CommandBehavior,CancellationToken)", Targets);
        }

        [Theory]
        [InlineData("ExecuteReader()")]
        [InlineData("ExecuteReader(CommandBehavior)")]
        [InlineData("ExecuteNonQuery()")]
        [InlineData("ExecuteScalar()")]
        [InlineData("ExecuteXmlReader()")]
        [InlineData("ExecuteNonQueryAsync(CancellationToken)")]
        [InlineData("ExecuteScalarAsync(CancellationToken)")]
        [InlineData("ExecuteReaderAsync(CancellationToken)")]
        public void Public_execution_methods_are_patched(string signature)
        {
            Assert.Contains(signature, Targets);
        }

        /// <summary>
        /// Inherited members are excluded. Harmony can only patch the implementation that
        /// actually runs, and for a <c>DbCommand</c> member that is the override.
        /// </summary>
        [Fact]
        public void Only_methods_SqlCommand_declares_are_targeted()
        {
            Assert.All(
                SqlCommandPatcher.FindTargets(),
                method => Assert.Equal(typeof(SqlCommand), method.DeclaringType));
        }

        [Fact]
        public void Nothing_unrelated_is_patched()
        {
            Assert.All(
                SqlCommandPatcher.FindTargets(),
                method => Assert.StartsWith("Execute", method.Name));
        }
    }
}
