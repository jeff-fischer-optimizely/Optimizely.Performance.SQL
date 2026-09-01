using System.Data;
using System.Threading.Tasks;
using Optimizely.Performance.SQL.Ado;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.Tests.Fakes;

namespace Optimizely.Performance.SQL.Tests
{
    /// <summary>
    /// The decorators end to end, over a fake provider that records what it was actually
    /// asked to execute. This is the only place the substitution can be observed: the
    /// scope puts the command back the way it found it before control returns.
    /// </summary>
    public class RewritingDbCommandTests
    {
        private readonly ExecutionLog _log = new ExecutionLog();

        private RewritingDbProviderFactory Factory(
            ApprovedSqlDocument document = null,
            RewriteOptions options = null,
            DatabaseCapabilities capabilities = null,
            IDatabaseCapabilityProvider provider = null)
        {
            var effectiveOptions = options ?? new RewriteOptions { AnnotateRewrittenSql = false };

            var registry = new SqlRewriteRegistry(
                document ?? Build.Document(Build.Statement()),
                effectiveOptions);

            var context = new RewriteContext(
                registry,
                effectiveOptions,
                provider ?? new StubCapabilityProvider(capabilities ?? Build.Capabilities()));

            return new RewritingDbProviderFactory(new FakeDbProviderFactory(_log), context);
        }

        /// <summary>An open connection and a command on it, exactly as the CMS would have.</summary>
        private (System.Data.Common.DbConnection Connection, System.Data.Common.DbCommand Command) Open(
            RewritingDbProviderFactory factory,
            string commandText,
            CommandType commandType = CommandType.Text)
        {
            var connection = factory.CreateConnection();
            connection.ConnectionString = "Server=fake;Database=FakeDb";
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.CommandType = commandType;

            return (connection, command);
        }

        // ---- the substitution ----------------------------------------------------

        [Fact]
        public void An_approved_statement_is_substituted_before_execution()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            command.ExecuteNonQuery();

            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        [Fact]
        public void An_unapproved_statement_reaches_the_provider_untouched()
        {
            const string sql = "SELECT * FROM tblSomethingElse";
            var (_, command) = Open(Factory(), sql);

            command.ExecuteNonQuery();

            Assert.Equal(sql, _log.Last.CommandText);
        }

        [Fact]
        public void The_command_is_restored_after_execution()
        {
            // EPiServer reuses command objects and reads CommandText back for its own
            // logging, so the caller must see exactly what it configured.
            var (_, command) = Open(Factory(), Build.OriginalSql);

            command.ExecuteNonQuery();

            Assert.Equal(Build.OriginalSql, command.CommandText);
        }

        [Fact]
        public void A_reused_command_is_rewritten_on_every_execution()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            command.ExecuteNonQuery();
            command.ExecuteNonQuery();

            Assert.Equal(2, _log.Entries.Count);
            Assert.All(_log.Entries, entry => Assert.Equal(Build.RewrittenSql, entry.CommandText));
        }

        [Theory]
        [InlineData("ExecuteNonQuery")]
        [InlineData("ExecuteScalar")]
        [InlineData("ExecuteReader")]
        public void Every_synchronous_execution_path_is_intercepted(string method)
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            switch (method)
            {
                case "ExecuteNonQuery":
                    command.ExecuteNonQuery();
                    break;
                case "ExecuteScalar":
                    command.ExecuteScalar();
                    break;
                default:
                    command.ExecuteReader().Dispose();
                    break;
            }

            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        [Fact]
        public async Task ExecuteNonQueryAsync_is_intercepted()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            await command.ExecuteNonQueryAsync();

            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
            Assert.Equal(Build.OriginalSql, command.CommandText);
        }

        [Fact]
        public async Task ExecuteScalarAsync_is_intercepted()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            await command.ExecuteScalarAsync();

            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        [Fact]
        public async Task ExecuteReaderAsync_is_intercepted()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql);

            using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(reader.Read());
            }

            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        // ---- pass-through paths --------------------------------------------------

        [Fact]
        public void A_TableDirect_command_is_never_touched()
        {
            var (_, command) = Open(Factory(), Build.OriginalSql, CommandType.TableDirect);

            command.ExecuteNonQuery();

            Assert.Equal(Build.OriginalSql, _log.Last.CommandText);
        }

        [Fact]
        public void An_empty_registry_short_circuits_before_any_probe()
        {
            var probe = new StubCapabilityProvider(Build.Capabilities());
            var factory = Factory(document: Build.Document(), provider: probe);

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Equal(0, probe.CallCount);
        }

        [Fact]
        public void A_disabled_shim_short_circuits_before_any_probe()
        {
            var probe = new StubCapabilityProvider(Build.Capabilities());

            var factory = Factory(
                options: new RewriteOptions { Enabled = false },
                provider: probe);

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Equal(0, probe.CallCount);
            Assert.Equal(Build.OriginalSql, _log.Last.CommandText);
        }

        [Fact]
        public void A_stored_procedure_call_costs_nothing_when_no_redirects_are_loaded()
        {
            // CMS 11 is sproc-heavy; the feature merely being present must not add a probe
            // to every call.
            var probe = new StubCapabilityProvider(Build.Capabilities());
            var factory = Factory(provider: probe);

            var (_, command) = Open(factory, "netContentLoad", CommandType.StoredProcedure);
            command.ExecuteNonQuery();

            Assert.Equal(0, probe.CallCount);
            Assert.Equal("netContentLoad", _log.Last.CommandText);
        }

        [Fact]
        public void Probing_can_be_switched_off_entirely()
        {
            var probe = new StubCapabilityProvider(Build.Capabilities());

            var factory = Factory(
                options: new RewriteOptions { ProbeDatabaseCapabilities = false, AnnotateRewrittenSql = false },
                provider: probe);

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Equal(0, probe.CallCount);

            // Unconditional rewrites still apply; anything with a precondition would not.
            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        [Fact]
        public void With_probing_off_a_conditional_rewrite_is_skipped()
        {
            var statement = Build.Statement(
                preconditions: new RewritePreconditions { RequiresCaseInsensitiveCollation = true });

            var factory = Factory(
                document: Build.Document(statement),
                options: new RewriteOptions { ProbeDatabaseCapabilities = false });

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Equal(Build.OriginalSql, _log.Last.CommandText);
        }

        // ---- failing open --------------------------------------------------------

        [Fact]
        public void A_probe_that_throws_leaves_the_original_statement_running()
        {
            // An unrewritten query is slow; a failed query is an outage.
            var factory = Factory(provider: new ThrowingCapabilityProvider());

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Equal(Build.OriginalSql, _log.Last.CommandText);
        }

        // ---- dropped parameters --------------------------------------------------

        private static ApprovedStatement LanguageStatement()
        {
            return Build.Statement(
                id: "OPT-0007",
                originalSql: "SELECT * FROM tblContentLanguage WHERE (@LanguageBranch IS NULL OR Name = @LanguageBranch)",
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "no-language",
                        When = new[]
                        {
                            new VariantCondition { Parameter = "LanguageBranch", Test = ParameterTest.IsNull }
                        },
                        Sql = "SELECT * FROM tblContentLanguage",
                        DropsParameters = new[] { "LanguageBranch" }
                    }
                });
        }

        [Fact]
        public void A_dropped_parameter_is_withheld_from_the_provider()
        {
            // SQL Server rejects a batch supplied with parameters it never declares.
            var factory = Factory(document: Build.Document(LanguageStatement()));
            var (_, command) = Open(factory, LanguageStatement().OriginalSql);

            AddParameter(command, "@ContentId", 42);
            AddParameter(command, "@LanguageBranch", System.DBNull.Value);
            AddParameter(command, "@Trailing", "x");

            command.ExecuteNonQuery();

            Assert.Equal("SELECT * FROM tblContentLanguage", _log.Last.CommandText);
            Assert.Equal(new[] { "@ContentId", "@Trailing" }, _log.Last.ParameterNames);
        }

        [Fact]
        public void A_dropped_parameter_is_put_back_in_its_original_position()
        {
            var factory = Factory(document: Build.Document(LanguageStatement()));
            var (_, command) = Open(factory, LanguageStatement().OriginalSql);

            AddParameter(command, "@ContentId", 42);
            AddParameter(command, "@LanguageBranch", System.DBNull.Value);
            AddParameter(command, "@Trailing", "x");

            command.ExecuteNonQuery();

            Assert.Equal(
                new[] { "@ContentId", "@LanguageBranch", "@Trailing" },
                Names(command));
        }

        [Fact]
        public void Restoration_survives_repeated_executions()
        {
            var factory = Factory(document: Build.Document(LanguageStatement()));
            var (_, command) = Open(factory, LanguageStatement().OriginalSql);

            AddParameter(command, "@ContentId", 42);
            AddParameter(command, "@LanguageBranch", System.DBNull.Value);
            AddParameter(command, "@Trailing", "x");

            command.ExecuteNonQuery();
            command.ExecuteNonQuery();
            command.ExecuteNonQuery();

            Assert.Equal(new[] { "@ContentId", "@LanguageBranch", "@Trailing" }, Names(command));
            Assert.All(_log.Entries, entry =>
                Assert.Equal(new[] { "@ContentId", "@Trailing" }, entry.ParameterNames));
        }

        // ---- procedure redirect through the decorator ----------------------------

        [Fact]
        public void A_stored_procedure_call_is_pointed_at_the_replacement()
        {
            const string body = "CREATE PROCEDURE dbo.netContentLoad AS SELECT 1";
            var hash = Fingerprinting.ModuleHash.Compute(body);

            var factory = Factory(
                document: Build.Document(Build.Redirect(originalBodyHash: hash)),
                capabilities: Build.Capabilities(procedures: Build.Procedures(
                    ("netContentLoad", hash),
                    ("netContentLoad_optiperf_v1", "x"))));

            var (_, command) = Open(factory, "netContentLoad", CommandType.StoredProcedure);
            command.ExecuteNonQuery();

            Assert.Equal("netContentLoad_optiperf_v1", _log.Last.CommandText);
            Assert.Equal(CommandType.StoredProcedure, _log.Last.CommandType);

            // And the caller still sees the name it asked for.
            Assert.Equal("netContentLoad", command.CommandText);
        }

        [Fact]
        public void An_undeployed_replacement_leaves_the_shipped_procedure_running()
        {
            const string body = "CREATE PROCEDURE dbo.netContentLoad AS SELECT 1";
            var hash = Fingerprinting.ModuleHash.Compute(body);

            var factory = Factory(
                document: Build.Document(Build.Redirect(originalBodyHash: hash)),
                capabilities: Build.Capabilities(procedures: Build.Procedures(("netContentLoad", hash))));

            var (_, command) = Open(factory, "netContentLoad", CommandType.StoredProcedure);
            command.ExecuteNonQuery();

            Assert.Equal("netContentLoad", _log.Last.CommandText);
        }

        // ---- plumbing ------------------------------------------------------------

        [Fact]
        public void The_factory_hands_out_decorated_connections_and_commands()
        {
            var factory = Factory();

            Assert.IsType<RewritingDbConnection>(factory.CreateConnection());
            Assert.IsType<RewritingDbCommand>(factory.CreateCommand());
            Assert.IsType<FakeDbParameter>(factory.CreateParameter());
        }

        [Fact]
        public void A_command_created_from_a_connection_reports_that_connection_back()
        {
            var factory = Factory();
            var connection = factory.CreateConnection();

            var command = connection.CreateCommand();

            Assert.Same(connection, command.Connection);
        }

        [Fact]
        public void The_provider_command_is_given_the_provider_connection_not_the_wrapper()
        {
            var factory = Factory();
            var connection = (RewritingDbConnection)factory.CreateConnection();

            var command = (RewritingDbCommand)connection.CreateCommand();

            Assert.Same(connection.InnerConnection, command.InnerCommand.Connection);
        }

        [Fact]
        public void A_transaction_is_unwrapped_before_it_reaches_the_provider_command()
        {
            var factory = Factory();
            var connection = (RewritingDbConnection)factory.CreateConnection();
            connection.Open();

            var transaction = (RewritingDbTransaction)connection.BeginTransaction();
            var command = (RewritingDbCommand)connection.CreateCommand();
            command.Transaction = transaction;

            Assert.Same(transaction, command.Transaction);
            Assert.Same(transaction.InnerTransaction, command.InnerCommand.Transaction);
        }

        [Fact]
        public void The_probe_is_told_which_transaction_the_command_is_in()
        {
            // SqlClient refuses to execute an unenlisted command on a connection holding
            // a pending local transaction. Since a failed probe is cached, dropping the
            // transaction here would disable every conditional rewrite on the database
            // for the rest of the process.
            var probe = new StubCapabilityProvider(Build.Capabilities());
            var factory = Factory(provider: probe);

            var connection = (RewritingDbConnection)factory.CreateConnection();
            connection.Open();

            var transaction = (RewritingDbTransaction)connection.BeginTransaction();
            var command = (RewritingDbCommand)connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Build.OriginalSql;

            command.ExecuteNonQuery();

            Assert.Equal(1, probe.CallCount);
            Assert.Same(transaction.InnerTransaction, probe.LastTransaction);
        }

        [Fact]
        public void The_probe_is_told_there_is_no_transaction_when_there_is_none()
        {
            var probe = new StubCapabilityProvider(Build.Capabilities());
            var factory = Factory(provider: probe);

            var (_, command) = Open(factory, Build.OriginalSql);
            command.ExecuteNonQuery();

            Assert.Null(probe.LastTransaction);
        }

        [Fact]
        public void A_transaction_reports_the_decorated_connection()
        {
            var factory = Factory();
            var connection = factory.CreateConnection();
            connection.Open();

            using (var transaction = connection.BeginTransaction())
            {
                Assert.Same(connection, transaction.Connection);
                transaction.Commit();
            }
        }

        [Fact]
        public void Connection_state_changes_are_forwarded_from_the_provider()
        {
            var factory = Factory();
            var connection = factory.CreateConnection();

            var observed = 0;
            connection.StateChange += (sender, args) => observed++;

            connection.Open();
            connection.Close();

            Assert.Equal(2, observed);
        }

        [Fact]
        public void Swapping_the_registry_takes_effect_on_the_next_command()
        {
            var options = new RewriteOptions { AnnotateRewrittenSql = false };
            var context = new RewriteContext(
                new SqlRewriteRegistry(Build.Document(), options),
                options,
                new StubCapabilityProvider(Build.Capabilities()));

            var factory = new RewritingDbProviderFactory(new FakeDbProviderFactory(_log), context);
            var (_, command) = Open(factory, Build.OriginalSql);

            command.ExecuteNonQuery();
            Assert.Equal(Build.OriginalSql, _log.Last.CommandText);

            context.SwapRegistry(new SqlRewriteRegistry(Build.Document(Build.Statement()), options));

            command.ExecuteNonQuery();
            Assert.Equal(Build.RewrittenSql, _log.Last.CommandText);
        }

        private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static string[] Names(System.Data.Common.DbCommand command)
        {
            var names = new string[command.Parameters.Count];

            for (var i = 0; i < names.Length; i++)
            {
                names[i] = command.Parameters[i].ParameterName;
            }

            return names;
        }
    }
}
