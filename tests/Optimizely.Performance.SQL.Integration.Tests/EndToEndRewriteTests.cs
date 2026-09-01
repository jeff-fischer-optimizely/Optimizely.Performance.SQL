using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Optimizely.Performance.SQL.Ado;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Integration.Tests
{
    /// <summary>
    /// The whole shim, over Microsoft.Data.SqlClient, against a real engine. The
    /// question these answer is the only one that finally matters: does the substituted
    /// statement return the same rows as the one the CMS wrote?
    /// </summary>
    [Collection("sqlserver")]
    public class EndToEndRewriteTests
    {
        private readonly SqlServerFixture _sql;

        public EndToEndRewriteTests(SqlServerFixture sql)
        {
            _sql = sql;
        }

        /// <summary>The rewrite under test: drop LOWER() on both sides, gated on collation.</summary>
        private static ApprovedStatement CaseFoldingRewrite()
        {
            return new ApprovedStatement
            {
                Id = "OPT-0001",
                Title = "Content by name: case-folded predicate cannot seek",
                OriginalSql = SqlServerFixture.OriginalSql,
                RewrittenSql = SqlServerFixture.RewrittenSql,
                Preconditions = new RewritePreconditions { RequiresCaseInsensitiveCollation = true }
            };
        }

        private static (RewritingDbProviderFactory Factory, RecordingObserver Observer) Shim(
            ApprovedSqlDocument document,
            RewriteOptions options = null)
        {
            var effective = options ?? new RewriteOptions();
            var observer = new RecordingObserver();
            var registry = new SqlRewriteRegistry(document, effective, observer);

            var context = new RewriteContext(
                registry,
                effective,
                new SqlServerCapabilityProvider(registry.ProcedureNames));

            return (new RewritingDbProviderFactory(SqlClientFactory.Instance, context), observer);
        }

        private static DbConnection Connect(DbProviderFactory factory, string connectionString)
        {
            var connection = factory.CreateConnection();
            connection.ConnectionString = connectionString;
            connection.Open();
            return connection;
        }

        private static List<string> Query(
            DbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                command.CommandType = CommandType.Text;

                foreach (var entry in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = entry.Name;
                    parameter.Value = entry.Value;
                    command.Parameters.Add(parameter);
                }

                using (var reader = command.ExecuteReader())
                {
                    return SqlServerFixture.ReadAll(reader);
                }
            }
        }

        // ---- the premise ---------------------------------------------------------

        [SqlServerFact]
        public void The_rewrite_is_equivalent_under_a_case_insensitive_collation()
        {
            // Establishing the baseline directly, with no shim involved: this is the fact
            // the approval record would assert.
            using (var connection = new SqlConnection(_sql.CaseInsensitiveConnectionString))
            {
                connection.Open();

                var original = Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus"));
                var rewritten = Query(connection, SqlServerFixture.RewrittenSql, ("@Name", "aboutus"));

                Assert.Equal(3, original.Count);
                Assert.Equal(original, rewritten);
            }
        }

        [SqlServerFact]
        public void The_rewrite_is_not_equivalent_under_a_case_sensitive_collation()
        {
            // And this is why the precondition exists. Without the interlock the shim
            // would turn three rows into one.
            using (var connection = new SqlConnection(_sql.CaseSensitiveConnectionString))
            {
                connection.Open();

                var original = Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus"));
                var rewritten = Query(connection, SqlServerFixture.RewrittenSql, ("@Name", "aboutus"));

                Assert.Equal(3, original.Count);
                Assert.Single(rewritten);
                Assert.NotEqual(original, rewritten);
            }
        }

        // ---- the shim ------------------------------------------------------------

        [SqlServerFact]
        public void The_shim_substitutes_and_returns_identical_rows()
        {
            var (factory, observer) = Shim(Document(CaseFoldingRewrite()));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus"));

                Assert.Equal(RewriteOutcome.Rewritten, observer.Last.Outcome);
                Assert.Equal("OPT-0001", observer.Last.StatementId);
                Assert.Equal(3, rows.Count);
                Assert.Equal(new[] { "1|aboutus", "2|AboutUs", "3|ABOUTUS" }, rows);
            }
        }

        [SqlServerFact]
        public void The_shim_stands_down_on_a_case_sensitive_database_and_results_are_preserved()
        {
            var (factory, observer) = Shim(Document(CaseFoldingRewrite()));

            using (var connection = Connect(factory, _sql.CaseSensitiveConnectionString))
            {
                var rows = Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus"));

                Assert.Equal(RewriteOutcome.PreconditionsNotMet, observer.Last.Outcome);

                // The original statement ran, so all three spellings still come back.
                Assert.Equal(3, rows.Count);
            }
        }

        [SqlServerFact]
        public void The_annotated_statement_executes_unchanged()
        {
            // The marker comment is what makes the substitution visible in Query Store,
            // so it has to survive contact with the parser.
            var (factory, _) = Shim(
                Document(CaseFoldingRewrite()),
                new RewriteOptions { AnnotateRewrittenSql = true });

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                Assert.Equal(3, Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus")).Count);
            }
        }

        [SqlServerFact]
        public void Shadow_mode_reports_the_rewrite_and_runs_the_original()
        {
            var (factory, observer) = Shim(
                Document(CaseFoldingRewrite()),
                new RewriteOptions { ShadowMode = true });

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, SqlServerFixture.OriginalSql, ("@Name", "aboutus"));

                Assert.Equal(RewriteOutcome.Shadowed, observer.Last.Outcome);
                Assert.Equal(3, rows.Count);
            }
        }

        [SqlServerFact]
        public void An_unapproved_statement_passes_straight_through()
        {
            var (factory, observer) = Shim(Document(CaseFoldingRewrite()));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, "SELECT pkID FROM dbo.tblContent WHERE ParentID = @p ORDER BY pkID", ("@p", 2));

                Assert.Empty(observer.Events);
                Assert.Equal(new[] { "4" }, rows);
            }
        }

        [SqlServerFact]
        public void A_scalar_execution_is_rewritten_too()
        {
            const string original = "SELECT COUNT(*) FROM dbo.tblContent WHERE LOWER(Name) = LOWER(@Name)";
            const string rewritten = "SELECT COUNT(*) FROM dbo.tblContent WHERE Name = @Name";

            var (factory, observer) = Shim(Document(new ApprovedStatement
            {
                Id = "OPT-0002",
                OriginalSql = original,
                RewrittenSql = rewritten
            }));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = original;

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@Name";
                parameter.Value = "aboutus";
                command.Parameters.Add(parameter);

                Assert.Equal(3, command.ExecuteScalar());
                Assert.Equal(RewriteOutcome.Rewritten, observer.Last.Outcome);
            }
        }

        [SqlServerFact]
        public void A_rewrite_inside_a_transaction_sees_uncommitted_rows()
        {
            // The decorators unwrap the transaction before handing it to the provider
            // command; get that wrong and the rewritten statement runs outside the
            // transaction, or fails outright.
            var (factory, observer) = Shim(Document(CaseFoldingRewrite()));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            using (var transaction = connection.BeginTransaction())
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO dbo.tblContent (Name, ParentID) VALUES (N'AbOuTuS', 1);";
                    insert.ExecuteNonQuery();
                }

                using (var select = connection.CreateCommand())
                {
                    select.Transaction = transaction;
                    select.CommandText = SqlServerFixture.OriginalSql;

                    var parameter = select.CreateParameter();
                    parameter.ParameterName = "@Name";
                    parameter.Value = "aboutus";
                    select.Parameters.Add(parameter);

                    using (var reader = select.ExecuteReader())
                    {
                        Assert.Equal(4, SqlServerFixture.ReadAll(reader).Count);
                    }
                }

                Assert.Equal(RewriteOutcome.Rewritten, observer.Last.Outcome);

                transaction.Rollback();
            }
        }

        // ---- variants and dropped parameters -------------------------------------

        private const string VariantOriginal =
            "SELECT pkID, Name FROM dbo.tblContent WHERE ParentID = @ParentID AND (@Name IS NULL OR Name = @Name) ORDER BY pkID";

        private static ApprovedStatement VariantRewrite()
        {
            return new ApprovedStatement
            {
                Id = "OPT-0007",
                OriginalSql = VariantOriginal,
                Variants = new[]
                {
                    new StatementVariant
                    {
                        Id = "no-name",
                        When = new[] { new VariantCondition { Parameter = "Name", Test = ParameterTest.IsNull } },
                        Sql = "SELECT pkID, Name FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID",
                        DropsParameters = new[] { "Name" }
                    },
                    new StatementVariant
                    {
                        Id = "name",
                        When = new[] { new VariantCondition { Parameter = "Name", Test = ParameterTest.IsNotNull } },
                        Sql = "SELECT pkID, Name FROM dbo.tblContent WHERE ParentID = @ParentID AND Name = @Name ORDER BY pkID"
                    }
                }
            };
        }

        [SqlServerFact]
        public void The_null_variant_executes_with_its_parameter_dropped()
        {
            var (factory, observer) = Shim(Document(VariantRewrite()));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, VariantOriginal, ("@ParentID", 1), ("@Name", System.DBNull.Value));

                Assert.Equal("OPT-0007/no-name", observer.Last.StatementId);
                Assert.Equal(RewriteOutcome.Rewritten, observer.Last.Outcome);
                Assert.Equal(3, rows.Count);
            }
        }

        [SqlServerFact]
        public void The_supplied_variant_executes_with_its_parameter_kept()
        {
            var (factory, observer) = Shim(Document(VariantRewrite()));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, VariantOriginal, ("@ParentID", 1), ("@Name", "AboutUs"));

                Assert.Equal("OPT-0007/name", observer.Last.StatementId);
                Assert.Equal(3, rows.Count); // case-insensitive collation: all three spellings
            }
        }

        [SqlServerFact]
        public void Both_variants_agree_with_the_statement_they_replace()
        {
            var (factory, _) = Shim(Document(VariantRewrite()));

            using (var direct = new SqlConnection(_sql.CaseInsensitiveConnectionString))
            using (var shimmed = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                direct.Open();

                foreach (var name in new object[] { System.DBNull.Value, "AboutUs" })
                {
                    Assert.Equal(
                        Query(direct, VariantOriginal, ("@ParentID", 1), ("@Name", name)),
                        Query(shimmed, VariantOriginal, ("@ParentID", 1), ("@Name", name)));
                }
            }
        }

        [SqlServerFact]
        public void A_variant_that_keeps_a_now_unused_parameter_still_executes()
        {
            // Documenting what the engine actually does with a parameter the batch never
            // references: sp_executesql tolerates it. dropsParameters is therefore about
            // plan hygiene and intent, not about avoiding a hard error.
            var statement = new ApprovedStatement
            {
                Id = "OPT-0008",
                OriginalSql = VariantOriginal,
                Variants = new[]
                {
                    new StatementVariant
                    {
                        Id = "no-name-but-kept",
                        When = new[] { new VariantCondition { Parameter = "Name", Test = ParameterTest.IsNull } },
                        Sql = "SELECT pkID, Name FROM dbo.tblContent WHERE ParentID = @ParentID ORDER BY pkID"
                    }
                }
            };

            var (factory, _) = Shim(Document(statement));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = Query(connection, VariantOriginal, ("@ParentID", 1), ("@Name", System.DBNull.Value));

                Assert.Equal(3, rows.Count);
            }
        }

        // ---- procedure redirects -------------------------------------------------

        private static List<string> CallProcedure(DbConnection connection, string name)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = name;
                command.CommandType = CommandType.StoredProcedure;

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@ParentID";
                parameter.Value = 1;
                command.Parameters.Add(parameter);

                using (var reader = command.ExecuteReader())
                {
                    return SqlServerFixture.ReadAll(reader);
                }
            }
        }

        private ApprovedStatement Redirect(string originalBodyHash, string replacement = "netContentLoad_optiperf_v1")
        {
            return new ApprovedStatement
            {
                Id = "OPT-9001",
                Kind = RewriteKind.ProcedureRedirect,
                Procedure = new ProcedureRedirect
                {
                    OriginalName = "netContentLoad",
                    ReplacementName = replacement,
                    OriginalBodyHash = originalBodyHash
                }
            };
        }

        [SqlServerFact]
        public void A_healthy_redirect_runs_the_replacement_procedure()
        {
            var (factory, observer) = Shim(Document(Redirect(_sql.OriginalProcedureHash)));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = CallProcedure(connection, "netContentLoad");

                Assert.Equal(RewriteOutcome.ProcedureRedirected, observer.Last.Outcome);
                Assert.Equal(3, rows.Count);
                Assert.All(rows, row => Assert.EndsWith("|replacement", row));
            }
        }

        [SqlServerFact]
        public void The_replacement_returns_what_the_original_returns()
        {
            using (var connection = new SqlConnection(_sql.CaseInsensitiveConnectionString))
            {
                connection.Open();

                var original = CallProcedure(connection, "netContentLoad");
                var replacement = CallProcedure(connection, "netContentLoad_optiperf_v1");

                // Same rows; only the marker column distinguishes them.
                Assert.Equal(
                    original.ConvertAll(row => row.Replace("|original", string.Empty)),
                    replacement.ConvertAll(row => row.Replace("|replacement", string.Empty)));
            }
        }

        [SqlServerFact]
        public void A_drifted_original_leaves_the_shipped_procedure_running()
        {
            // The recorded hash no longer matches what is deployed: a CMS upgrade has
            // moved the procedure underneath us.
            var stale = Fingerprinting.ModuleHash.Compute("CREATE PROCEDURE dbo.netContentLoad AS SELECT 'old'");

            var (factory, observer) = Shim(Document(Redirect(stale)));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = CallProcedure(connection, "netContentLoad");

                Assert.Equal(RewriteOutcome.OriginalProcedureDrifted, observer.Last.Outcome);
                Assert.All(rows, row => Assert.EndsWith("|original", row));
            }
        }

        [SqlServerFact]
        public void An_undeployed_replacement_leaves_the_shipped_procedure_running()
        {
            // The state of every site where the installer has not run yet.
            var (factory, observer) = Shim(
                Document(Redirect(_sql.OriginalProcedureHash, "netContentLoad_optiperf_v99")));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = CallProcedure(connection, "netContentLoad");

                Assert.Equal(RewriteOutcome.ReplacementProcedureMissing, observer.Last.Outcome);
                Assert.All(rows, row => Assert.EndsWith("|original", row));
            }
        }

        [SqlServerFact]
        public void A_procedure_not_under_redirect_is_untouched()
        {
            var (factory, observer) = Shim(Document(Redirect(_sql.OriginalProcedureHash)));

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "netOrphanLoad";
                command.CommandType = CommandType.StoredProcedure;

                Assert.Equal(1, command.ExecuteScalar());
                Assert.Empty(observer.Events);
            }
        }

        [SqlServerFact]
        public void Shadow_mode_leaves_the_shipped_procedure_running()
        {
            var (factory, observer) = Shim(
                Document(Redirect(_sql.OriginalProcedureHash)),
                new RewriteOptions { ShadowMode = true });

            using (var connection = Connect(factory, _sql.CaseInsensitiveConnectionString))
            {
                var rows = CallProcedure(connection, "netContentLoad");

                Assert.Equal(RewriteOutcome.Shadowed, observer.Last.Outcome);
                Assert.All(rows, row => Assert.EndsWith("|original", row));
            }
        }

        private static ApprovedSqlDocument Document(params ApprovedStatement[] statements)
        {
            return new ApprovedSqlDocument { SchemaVersion = 1, Statements = statements };
        }

        private sealed class RecordingObserver : IRewriteObserver
        {
            private readonly List<RewriteEvent> _events = new List<RewriteEvent>();

            public IReadOnlyList<RewriteEvent> Events
            {
                get { return _events; }
            }

            public RewriteEvent Last
            {
                get { return _events.Count == 0 ? null : _events[_events.Count - 1]; }
            }

            public void OnRewriteEvaluated(RewriteEvent evaluation)
            {
                _events.Add(evaluation);
            }
        }
    }
}
