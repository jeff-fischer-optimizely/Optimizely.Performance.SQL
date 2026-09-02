using System;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Optimizely.Performance.SQL.V11;
using Xunit;

namespace Optimizely.Performance.SQL.V11.Tests
{
    /// <summary>
    /// The shim installed against a real SQL Server, asked the only question that matters:
    /// did the approved SQL reach the server?
    /// </summary>
    /// <remarks>
    /// Every assertion here reads a value the <em>server</em> produced. Checking that the
    /// shim set <c>CommandText</c> would prove nothing — the command is restored before the
    /// caller can look at it, and a substitution the provider ignored would look identical.
    /// </remarks>
    [Collection("shim")]
    public sealed class InterceptionTests
    {
        private readonly ShimFixture _fixture;

        public InterceptionTests(ShimFixture fixture)
        {
            _fixture = fixture;

            Assert.True(_fixture.Available, "fixture setup failed: " + _fixture.SetupError);
        }

        [SqlServerFact]
        public void Approved_statement_runs_its_replacement()
        {
            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            using (var reader = command.ExecuteReader())
            {
                Assert.Equal("replacement", ShimFixture.SourceOf(reader));
            }
        }

        [SqlServerFact]
        public void Unapproved_statement_runs_unchanged()
        {
            using (var connection = _fixture.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = ShimFixture.UnapprovedSql;
                command.Parameters.AddWithValue("@pkID", 1);

                using (var reader = command.ExecuteReader())
                {
                    Assert.Equal("original", ShimFixture.SourceOf(reader));
                }
            }
        }

        /// <summary>
        /// The call shape at roughly 97 CMS 11 sites, and the one that a public-methods-only
        /// patch set silently misses.
        /// </summary>
        [SqlServerFact]
        public void Caller_holding_a_DbCommand_gets_the_replacement()
        {
            using (var connection = _fixture.Open())
            {
                DbCommand command = connection.CreateCommand();
                command.CommandText = ShimFixture.OriginalSql;

                var parameter = command.CreateParameter();
                parameter.ParameterName = "@ParentID";
                parameter.Value = 1;
                command.Parameters.Add(parameter);

                using (var reader = command.ExecuteReader())
                {
                    Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                }
            }
        }

        /// <summary>
        /// EPiServer's actual idiom: take a <c>DbCommand</c> from the executor, cast it to
        /// <c>SqlCommand</c> to reach a provider-specific member, run it.
        /// </summary>
        /// <remarks>
        /// This is why the shim edits the command instead of wrapping it. <c>SqlCommand</c>
        /// is sealed, so a decorator would throw an <see cref="InvalidCastException"/> right
        /// here, at 25 sites in CMS 11.
        /// </remarks>
        [SqlServerFact]
        public void Hard_cast_to_SqlCommand_still_works()
        {
            using (var connection = _fixture.Open())
            {
                DbCommand command = connection.CreateCommand();
                command.CommandText = ShimFixture.OriginalSql;
                command.Parameters.Add(new SqlParameter("@ParentID", 1));

                var native = (SqlCommand)command;

                using (var reader = native.ExecuteReader())
                {
                    Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                }
            }
        }

        [SqlServerFact]
        public async Task Async_execution_runs_the_replacement()
        {
            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("replacement", reader.GetString(0));
            }
        }

        [SqlServerFact]
        public void ExecuteScalar_runs_the_replacement()
        {
            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            {
                Assert.Equal("replacement", command.ExecuteScalar());
            }
        }

        /// <summary>
        /// The non-query path leaves no result to read, so the proof is the row it wrote.
        /// </summary>
        [SqlServerFact]
        public void ExecuteNonQuery_runs_the_replacement()
        {
            using (var connection = _fixture.Open())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = ShimFixture.UpdateOriginalSql;
                    command.Parameters.AddWithValue("@pkID", ShimFixture.ScratchRowId);
                    command.ExecuteNonQuery();
                }

                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "SELECT Name FROM dbo.tblContent WHERE pkID = @pkID";
                    check.Parameters.AddWithValue("@pkID", ShimFixture.ScratchRowId);

                    Assert.Equal("replacement", check.ExecuteScalar());
                }
            }
        }

        /// <summary>
        /// The redirect names a versioned copy deployed alongside the shipped procedure.
        /// Optimizely's own procedure is never touched — that is the constraint the whole
        /// procedure-redirect design exists to satisfy.
        /// </summary>
        [SqlServerFact]
        public void Procedure_call_is_redirected_to_the_versioned_copy()
        {
            using (var connection = _fixture.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandType = CommandType.StoredProcedure;
                command.CommandText = ShimFixture.OriginalProcedure;
                command.Parameters.AddWithValue("@ParentID", 1);

                using (var reader = command.ExecuteReader())
                {
                    Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                }
            }
        }

        [SqlServerFact]
        public void Command_is_restored_after_execution()
        {
            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            {
                using (var reader = command.ExecuteReader())
                {
                    Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                }

                // EPiServer reads CommandText back for its own logging and reuses command
                // objects, so it must see what it set.
                Assert.Equal(ShimFixture.OriginalSql, command.CommandText);
            }
        }

        /// <summary>
        /// A failed execution must restore too. EPiServer retries deadlocks on the same
        /// command object, and a retry that inherits a half-applied rewrite fails for a
        /// reason that has nothing to do with the original error.
        /// </summary>
        [SqlServerFact]
        public void Command_is_restored_after_a_failed_execution()
        {
            using (var connection = _fixture.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = ShimFixture.OriginalSql;
                // No @ParentID, so the server rejects the batch.

                Assert.Throws<SqlException>(() => command.ExecuteReader());
                Assert.Equal(ShimFixture.OriginalSql, command.CommandText);
            }
        }

        [SqlServerFact]
        public void Reusing_a_command_rewrites_every_execution()
        {
            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            {
                for (var i = 0; i < 3; i++)
                {
                    using (var reader = command.ExecuteReader())
                    {
                        Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                    }

                    Assert.Equal(ShimFixture.OriginalSql, command.CommandText);
                }
            }
        }

        [SqlServerFact]
        public void Rewriting_works_inside_a_transaction()
        {
            using (var connection = _fixture.Open())
            using (var transaction = connection.BeginTransaction())
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = ShimFixture.OriginalSql;
                    command.Parameters.AddWithValue("@ParentID", 1);

                    using (var reader = command.ExecuteReader())
                    {
                        Assert.Equal("replacement", ShimFixture.SourceOf(reader));
                    }
                }

                transaction.Rollback();
            }
        }

        /// <summary>
        /// One logical execution must produce one rewrite.
        /// </summary>
        /// <remarks>
        /// SqlCommand's execution methods call each other — <c>ExecuteReader()</c> reaches
        /// <c>ExecuteReader(CommandBehavior)</c>, and so does <c>ExecuteDbDataReader</c> —
        /// so a patch set without a reentrancy guard applies the rewrite on top of itself,
        /// fingerprinting text the shim produced a moment earlier.
        /// </remarks>
        [SqlServerFact]
        public void One_execution_produces_one_rewrite()
        {
            var before = PerformanceSqlShim.Applied;

            using (var connection = _fixture.Open())
            using (var command = Text(connection, ShimFixture.OriginalSql))
            using (var reader = command.ExecuteReader())
            {
                Assert.Equal("replacement", ShimFixture.SourceOf(reader));
            }

            Assert.Equal(1, PerformanceSqlShim.Applied - before);
        }

        [SqlServerFact]
        public void Every_rewrite_is_undone()
        {
            using (var connection = _fixture.Open())
            {
                using (var command = Text(connection, ShimFixture.OriginalSql))
                {
                    command.ExecuteReader().Dispose();
                }

                using (var command = connection.CreateCommand())
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = ShimFixture.OriginalProcedure;
                    command.Parameters.AddWithValue("@ParentID", 1);
                    command.ExecuteReader().Dispose();
                }
            }

            Assert.Equal(PerformanceSqlShim.Applied, PerformanceSqlShim.Restored);
            Assert.Equal(0, PerformanceSqlShim.RestoreFailures);
        }

        [SqlServerFact]
        public void Install_reports_what_it_patched()
        {
            Assert.True(_fixture.Status.Installed, _fixture.Status.ToString());
            Assert.Empty(_fixture.Status.Failures);
            Assert.Null(_fixture.Status.LoadError);
            Assert.Equal(3, _fixture.Status.StatementCount);
        }

        private static SqlCommand Text(SqlConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@ParentID", 1);
            return command;
        }
    }
}
