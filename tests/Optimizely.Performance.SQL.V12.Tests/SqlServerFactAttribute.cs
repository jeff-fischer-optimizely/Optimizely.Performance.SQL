using System;
using Microsoft.Data.SqlClient;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Optimizely.Performance.SQL.V12.Tests
{
    /// <summary>
    /// A fact that skips itself when there is no reachable SQL Server, so the suite is
    /// still runnable on a machine that has not got one.
    /// </summary>
    public sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (SqlServerAvailability.Reason.Length != 0)
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
            try
            {
                using (var connection = new SqlConnection(TestDatabase.MasterConnectionString))
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

    internal static class TestDatabase
    {
        public static string MasterConnectionString
        {
            get
            {
                return Environment.GetEnvironmentVariable("OPTIPERF_TEST_SQL")
                    ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";
            }
        }
    }
}
