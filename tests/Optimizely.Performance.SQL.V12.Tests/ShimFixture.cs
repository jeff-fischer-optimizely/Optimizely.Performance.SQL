using System;
using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Fingerprinting;
using Xunit;

namespace Optimizely.Performance.SQL.V12.Tests
{
    /// <summary>
    /// A scratch database, an approved-SQL file describing it, and the shim installed
    /// against both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared across the assembly because subscribing to the SqlClient diagnostic source is
    /// a process-wide act, exactly as patching is on CMS 11.
    /// </para>
    /// <para>
    /// The approved statements are contrived so the two versions return <em>different</em>
    /// data — a <c>Source</c> column reading either <c>original</c> or <c>replacement</c>.
    /// Real rewrites are semantically identical, which makes them useless for proving one
    /// actually reached the server. These are the miniature form of watching a profiler.
    /// </para>
    /// </remarks>
    public sealed class ShimFixture : IDisposable
    {
        private const string Database = "OptiPerfV12Test";

        /// <summary>The statement the CMS is pretending to emit.</summary>
        public const string OriginalSql =
            "SELECT CONVERT(nvarchar(20), 'original') AS Source, pkID FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID";

        /// <summary>Its approved replacement, distinguishable in the result set.</summary>
        public const string RewrittenSql =
            "SELECT CONVERT(nvarchar(20), 'replacement') AS Source, pkID FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID";

        /// <summary>A statement with no approval, which must pass through untouched.</summary>
        public const string UnapprovedSql =
            "SELECT CONVERT(nvarchar(20), 'original') AS Source, pkID FROM dbo.tblContent WHERE pkID = @pkID";

        /// <summary>
        /// An approved pair for the non-query path, where the only way to see which version
        /// ran is the row it leaves behind.
        /// </summary>
        public const string UpdateOriginalSql =
            "UPDATE dbo.tblContent SET Name = N'original' WHERE pkID = @pkID";

        public const string UpdateRewrittenSql =
            "UPDATE dbo.tblContent SET Name = N'replacement' WHERE pkID = @pkID";

        /// <summary>The row the non-query tests are allowed to scribble on.</summary>
        public const int ScratchRowId = 3;

        public const string OriginalProcedure = "netContentLoad";
        public const string ReplacementProcedure = "netContentLoad_optiperf_v1";

        private readonly string _configurationDirectory;

        public ShimFixture()
        {
            _configurationDirectory = Path.Combine(Path.GetTempPath(), "optiperf-v12-" + Guid.NewGuid().ToString("n"));

            try
            {
                CreateDatabase();

                Directory.CreateDirectory(Path.Combine(_configurationDirectory, "config"));
                File.WriteAllText(ConfigurationFile, BuildApprovedSql());

                var status = PerformanceSqlShim.Install(
                    new RewriteOptions { ConfigurationPath = "config/approved-sql.json" },
                    observer: null,
                    baseDirectory: _configurationDirectory);

                if (!status.Installed)
                {
                    throw new InvalidOperationException("shim did not install: " + status);
                }

                Status = status;
                Available = true;
            }
            catch (Exception ex)
            {
                SetupError = ex.Message;
                Available = false;
            }
        }

        /// <summary>False when there is no reachable SQL Server or the shim would not install.</summary>
        public bool Available { get; }

        public string SetupError { get; }

        public ShimStatus Status { get; }

        public string ConnectionString
        {
            get
            {
                return new SqlConnectionStringBuilder(TestDatabase.MasterConnectionString) { InitialCatalog = Database }
                    .ConnectionString;
            }
        }

        private string ConfigurationFile
        {
            get { return Path.Combine(_configurationDirectory, "config", "approved-sql.json"); }
        }

        public SqlConnection Open()
        {
            var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Reads the <c>Source</c> column of the first row, which says which version of the
        /// statement the server actually ran.
        /// </summary>
        public static string SourceOf(IDataReader reader)
        {
            return reader.Read() ? reader.GetString(0) : "(no rows)";
        }

        // ---- schema --------------------------------------------------------------

        private static void CreateDatabase()
        {
            Execute(TestDatabase.MasterConnectionString, Drop());
            Execute(TestDatabase.MasterConnectionString, "CREATE DATABASE [" + Database + "];");

            var connectionString =
                new SqlConnectionStringBuilder(TestDatabase.MasterConnectionString) { InitialCatalog = Database }
                    .ConnectionString;

            Execute(connectionString, @"
CREATE TABLE dbo.tblContent
(
    pkID     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_tblContent PRIMARY KEY,
    Name     nvarchar(255) NOT NULL,
    ParentID int NOT NULL
);");

            Execute(connectionString, @"
INSERT INTO dbo.tblContent (Name, ParentID) VALUES
    (N'AboutUs', 1),
    (N'Contact', 1),
    (N'News', 2);");

            Execute(connectionString, @"
CREATE PROCEDURE dbo.netContentLoad
    @ParentID int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CONVERT(nvarchar(20), 'original') AS Source, pkID FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID;
END;");

            Execute(connectionString, @"
CREATE PROCEDURE dbo.netContentLoad_optiperf_v1
    @ParentID int
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CONVERT(nvarchar(20), 'replacement') AS Source, pkID FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID;
END;");
        }

        private static string Drop()
        {
            return @"
IF DB_ID('" + Database + @"') IS NOT NULL
BEGIN
    ALTER DATABASE [" + Database + @"] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [" + Database + @"];
END";
        }

        /// <summary>
        /// Writes the approved-SQL document, hashing the shipped procedure body exactly as
        /// the offline sync tool would.
        /// </summary>
        private string BuildApprovedSql()
        {
            var document = new ApprovedSqlDocument
            {
                GeneratedBy = "Optimizely.Performance.SQL.V12.Tests",
                Statements = new[]
                {
                    new ApprovedStatement
                    {
                        Id = "V12-0001",
                        Title = "statement rewrite",
                        Kind = RewriteKind.StatementRewrite,
                        OriginalSql = OriginalSql,
                        RewrittenSql = RewrittenSql,
                        AppliesTo = CmsVersion.All,
                        Enabled = true
                    },
                    new ApprovedStatement
                    {
                        Id = "V12-0002",
                        Title = "non-query rewrite",
                        Kind = RewriteKind.StatementRewrite,
                        OriginalSql = UpdateOriginalSql,
                        RewrittenSql = UpdateRewrittenSql,
                        AppliesTo = CmsVersion.All,
                        Enabled = true
                    },
                    new ApprovedStatement
                    {
                        Id = "V12-9001",
                        Title = "procedure redirect",
                        Kind = RewriteKind.ProcedureRedirect,
                        Procedure = new ProcedureRedirect
                        {
                            OriginalName = OriginalProcedure,
                            ReplacementName = ReplacementProcedure,
                            OriginalBodyHash = ReadProcedureHash(OriginalProcedure)
                        },
                        AppliesTo = CmsVersion.All,
                        Enabled = true
                    }
                }
            };

            return ApprovedSqlLoader.Serialize(document);
        }

        private string ReadProcedureHash(string procedureName)
        {
            using (var connection = new SqlConnection(ConnectionString))
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

        public void Dispose()
        {
            PerformanceSqlShim.Uninstall();

            try
            {
                if (Directory.Exists(_configurationDirectory))
                {
                    Directory.Delete(_configurationDirectory, true);
                }
            }
            catch (IOException)
            {
            }

            if (!Available)
            {
                return;
            }

            try
            {
                SqlConnection.ClearAllPools();
                Execute(TestDatabase.MasterConnectionString, Drop());
            }
            catch (SqlException)
            {
                // A scratch database left behind is untidy, not a test failure.
            }
        }
    }

    [CollectionDefinition("shim")]
    public sealed class ShimCollection : ICollectionFixture<ShimFixture>
    {
    }
}
