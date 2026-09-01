using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using Optimizely.Performance.SQL.Fingerprinting;

namespace Optimizely.Performance.SQL.Integration.Tests
{
    /// <summary>
    /// Two scratch databases on a real SQL Server: one case-insensitive, one
    /// case-sensitive. The pair is the point — the collation precondition is the
    /// interlock protecting every rewrite that drops a case-folding function, and the
    /// only way to show it works is to run the same rewrite against both and watch it
    /// stand down on one of them.
    /// </summary>
    public sealed class SqlServerFixture : IDisposable
    {
        public const string CaseInsensitiveCollation = "SQL_Latin1_General_CP1_CI_AS";
        public const string CaseSensitiveCollation = "SQL_Latin1_General_CP1_CS_AS";

        private const string CaseInsensitiveDatabase = "OptiPerfSqlTest_CI";
        private const string CaseSensitiveDatabase = "OptiPerfSqlTest_CS";

        /// <summary>The statement the CMS is pretending to emit: a predicate that cannot seek.</summary>
        public const string OriginalSql =
            "SELECT pkID, Name FROM dbo.tblContent WHERE LOWER(Name) = LOWER(@Name) ORDER BY pkID";

        /// <summary>Its approved replacement, valid only under a case-insensitive collation.</summary>
        public const string RewrittenSql =
            "SELECT pkID, Name FROM dbo.tblContent WHERE Name = @Name ORDER BY pkID";

        public SqlServerFixture()
        {
            try
            {
                Create(CaseInsensitiveDatabase, CaseInsensitiveCollation);
                Create(CaseSensitiveDatabase, CaseSensitiveCollation);

                OriginalProcedureHash = ReadProcedureHash(CaseInsensitiveConnectionString, "netContentLoad");
                Available = true;
            }
            catch (Exception ex)
            {
                SetupError = ex.Message;
                Available = false;
            }
        }

        /// <summary>False when there is no reachable SQL Server; the tests skip rather than fail.</summary>
        public bool Available { get; }

        public string SetupError { get; }

        /// <summary>Hash of the shipped procedure body, as the sync tool would record it.</summary>
        public string OriginalProcedureHash { get; }

        public string CaseInsensitiveConnectionString
        {
            get { return ConnectionStringFor(CaseInsensitiveDatabase); }
        }

        public string CaseSensitiveConnectionString
        {
            get { return ConnectionStringFor(CaseSensitiveDatabase); }
        }

        /// <summary>
        /// Server to test against. Override with <c>OPTIPERF_TEST_SQL</c> to point at
        /// something other than a local default instance.
        /// </summary>
        private static string MasterConnectionString
        {
            get
            {
                return Environment.GetEnvironmentVariable("OPTIPERF_TEST_SQL")
                    ?? "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5";
            }
        }

        private static string ConnectionStringFor(string database)
        {
            return new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = database }
                .ConnectionString;
        }

        public SqlConnection OpenCaseInsensitive()
        {
            var connection = new SqlConnection(CaseInsensitiveConnectionString);
            connection.Open();
            return connection;
        }

        public SqlConnection OpenCaseSensitive()
        {
            var connection = new SqlConnection(CaseSensitiveConnectionString);
            connection.Open();
            return connection;
        }

        // ---- schema --------------------------------------------------------------

        private static void Create(string database, string collation)
        {
            Execute(MasterConnectionString, Drop(database));

            // COLLATE on the database, so the Name column inherits it and the comparison
            // semantics under test are the real ones rather than a column-level override.
            Execute(MasterConnectionString, $"CREATE DATABASE [{database}] COLLATE {collation};");

            var connectionString = ConnectionStringFor(database);

            Execute(connectionString, @"
CREATE TABLE dbo.tblContent
(
    pkID     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblContent PRIMARY KEY,
    Name     nvarchar(255) NOT NULL,
    ParentID int NOT NULL
);");

            Execute(connectionString, "CREATE INDEX IX_tblContent_Name ON dbo.tblContent (Name);");

            // Three spellings of one name, so a case-folded predicate and a plain equality
            // agree under CI and disagree under CS.
            Execute(connectionString, @"
INSERT INTO dbo.tblContent (Name, ParentID) VALUES
    (N'aboutus', 1),
    (N'AboutUs', 1),
    (N'ABOUTUS', 1),
    (N'Contact', 2);");

            Execute(connectionString, @"
CREATE PROCEDURE dbo.netContentLoad
    @ParentID int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT pkID, Name, CONVERT(nvarchar(20), 'original') AS Source
    FROM dbo.tblContent
    WHERE ParentID = @ParentID
    ORDER BY pkID;
END;");

            // The optimised body, deployed alongside rather than over the top. Its Source
            // column is the only difference, so a test can see which one ran.
            Execute(connectionString, @"
CREATE PROCEDURE dbo.netContentLoad_optiperf_v1
    @ParentID int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT pkID, Name, CONVERT(nvarchar(20), 'replacement') AS Source
    FROM dbo.tblContent
    WHERE ParentID = @ParentID
    ORDER BY pkID;
END;");

            // A procedure with no replacement deployed, for the not-installed case.
            Execute(connectionString, @"
CREATE PROCEDURE dbo.netOrphanLoad
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 1 AS value;
END;");

            // An unhashable body: sys.sql_modules.definition comes back NULL.
            Execute(connectionString, @"
CREATE PROCEDURE dbo.netEncryptedLoad
WITH ENCRYPTION
AS
BEGIN
    SET NOCOUNT ON;
    SELECT 1 AS value;
END;");
        }

        private static string Drop(string database)
        {
            return $@"
IF DB_ID('{database}') IS NOT NULL
BEGIN
    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{database}];
END";
        }

        /// <summary>
        /// Reads a procedure body straight from <c>sys.sql_modules</c> and hashes it, which
        /// is exactly what the offline sync tool does. If the runtime probe and this
        /// disagree, no redirect would ever fire.
        /// </summary>
        private static string ReadProcedureHash(string connectionString, string procedureName)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "SELECT m.definition FROM sys.sql_modules m" +
                        " JOIN sys.objects o ON o.object_id = m.object_id" +
                        " WHERE o.name = @name";

                    command.Parameters.AddWithValue("@name", procedureName);

                    return ModuleHash.Compute((string)command.ExecuteScalar());
                }
            }
        }

        private static void Execute(string connectionString, string sql)
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    command.CommandType = CommandType.Text;
                    command.CommandTimeout = 30;
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>Rows from a reader, as pipe-joined strings, for terse comparison.</summary>
        public static List<string> ReadAll(IDataReader reader)
        {
            var rows = new List<string>();

            while (reader.Read())
            {
                var values = new string[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i));
                }

                rows.Add(string.Join("|", values));
            }

            return rows;
        }

        public void Dispose()
        {
            if (!Available)
            {
                return;
            }

            try
            {
                SqlConnection.ClearAllPools();
                Execute(MasterConnectionString, Drop(CaseInsensitiveDatabase));
                Execute(MasterConnectionString, Drop(CaseSensitiveDatabase));
            }
            catch (SqlException)
            {
                // A scratch database left behind is untidy, not a test failure.
            }
        }
    }

    [CollectionDefinition("sqlserver")]
    public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
    {
    }
}
