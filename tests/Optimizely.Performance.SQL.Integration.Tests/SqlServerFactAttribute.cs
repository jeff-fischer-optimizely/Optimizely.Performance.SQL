using System;
using Microsoft.Data.SqlClient;

namespace Optimizely.Performance.SQL.Integration.Tests
{
    /// <summary>
    /// A fact that skips itself when there is no reachable SQL Server, so the suite is
    /// still runnable on a machine that has not got one.
    /// </summary>
    public sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (!SqlServerAvailability.Reason.Equals(string.Empty, StringComparison.Ordinal))
            {
                Skip = SqlServerAvailability.Reason;
            }
        }
    }

    /// <summary>A theory that skips itself when there is no reachable SQL Server.</summary>
    public sealed class SqlServerTheoryAttribute : TheoryAttribute
    {
        public SqlServerTheoryAttribute()
        {
            if (!SqlServerAvailability.Reason.Equals(string.Empty, StringComparison.Ordinal))
            {
                Skip = SqlServerAvailability.Reason;
            }
        }
    }

    internal static class SqlServerAvailability
    {
        private static readonly Lazy<string> Probe = new Lazy<string>(Check);

        /// <summary>Empty when a server answered; otherwise the skip reason.</summary>
        public static string Reason
        {
            get { return Probe.Value; }
        }

        private static string Check()
        {
            var connectionString = Environment.GetEnvironmentVariable("OPTIPERF_TEST_SQL")
                ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                return "No SQL Server reachable (set OPTIPERF_TEST_SQL to override): " + ex.Message;
            }
        }
    }
}
