using System.Data;
using Microsoft.Data.SqlClient;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Integration.Tests
{
    /// <summary>
    /// The probe against a real engine. Everything the preconditions are checked against
    /// comes from here, so if this reads the wrong thing the interlock is decorative.
    /// </summary>
    [Collection("sqlserver")]
    public class CapabilityProbeTests
    {
        private readonly SqlServerFixture _sql;

        public CapabilityProbeTests(SqlServerFixture sql)
        {
            _sql = sql;
        }

        [SqlServerFact]
        public void Fixture_databases_were_created()
        {
            Assert.True(_sql.Available, _sql.SetupError);
        }

        [SqlServerFact]
        public void The_probe_reads_collation_version_and_compatibility_level()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.True(capabilities.IsProbed);
                Assert.Equal(SqlServerFixture.CaseInsensitiveCollation, capabilities.Collation);
                Assert.True(capabilities.IsCaseInsensitive);
                Assert.False(capabilities.IsAccentInsensitive);
                Assert.True(capabilities.SqlServerMajorVersion >= 11, "major version " + capabilities.SqlServerMajorVersion);
                Assert.True(capabilities.CompatibilityLevel >= 100, "compat level " + capabilities.CompatibilityLevel);
            }
        }

        [SqlServerFact]
        public void The_probe_distinguishes_a_case_sensitive_database()
        {
            using (var connection = _sql.OpenCaseSensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.Equal(SqlServerFixture.CaseSensitiveCollation, capabilities.Collation);
                Assert.False(capabilities.IsCaseInsensitive);
            }
        }

        [SqlServerFact]
        public void The_probe_reads_index_names_from_sys_indexes()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.True(capabilities.HasIndex("IX_tblContent_Name"));
                Assert.True(capabilities.HasIndex("PK_tblContent"));
                Assert.False(capabilities.HasIndex("IX_that_does_not_exist"));
            }
        }

        [SqlServerFact]
        public void An_index_precondition_is_evaluated_against_the_live_schema()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.True(capabilities.Satisfies(new RewritePreconditions
                {
                    RequiredIndexes = new[] { "IX_tblContent_Name" }
                }));

                Assert.False(capabilities.Satisfies(new RewritePreconditions
                {
                    RequiredIndexes = new[] { "IX_not_deployed_yet" }
                }));
            }
        }

        [SqlServerFact]
        public void An_unreachable_compatibility_level_blocks_the_rewrite()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.False(capabilities.Satisfies(new RewritePreconditions
                {
                    MinimumCompatibilityLevel = 999
                }));
            }
        }

        [SqlServerFact]
        public void No_procedure_metadata_is_fetched_when_nothing_is_under_redirect()
        {
            // An EPiServer database holds hundreds of procedures running to megabytes of
            // body text; the probe must not read them speculatively.
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.False(capabilities.HasProcedure("netContentLoad"));
            }
        }

        [SqlServerFact]
        public void Procedures_of_interest_are_read_and_hashed()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var provider = new SqlServerCapabilityProvider(
                    new[] { "netContentLoad", "netContentLoad_optiperf_v1" });

                var capabilities = provider.GetCapabilities(connection);

                Assert.True(capabilities.HasProcedure("netContentLoad"));
                Assert.True(capabilities.HasProcedure("netContentLoad_optiperf_v1"));
                Assert.False(capabilities.HasProcedure("netOrphanLoad"));
            }
        }

        [SqlServerFact]
        public void The_runtime_hash_agrees_with_the_hash_taken_offline()
        {
            // The sync tool records the hash from sys.sql_modules ahead of time and the
            // runtime recomputes it from the live database. If these two ever diverge,
            // every redirect silently reads as drift and nothing is ever rewritten.
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var provider = new SqlServerCapabilityProvider(new[] { "netContentLoad" });
                var capabilities = provider.GetCapabilities(connection);

                Assert.True(capabilities.ProcedureBodyMatches("netContentLoad", _sql.OriginalProcedureHash));
            }
        }

        [SqlServerFact]
        public void Procedure_names_are_resolved_however_the_configuration_spells_them()
        {
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var provider = new SqlServerCapabilityProvider(new[] { "[dbo].[netContentLoad]" });
                var capabilities = provider.GetCapabilities(connection);

                Assert.True(capabilities.HasProcedure("netContentLoad"));
            }
        }

        [SqlServerFact]
        public void A_closed_connection_is_not_probed()
        {
            // Opening one here could deadlock inside a provider callback or exhaust the
            // pool, so an unopened connection has to come back Unknown.
            using (var connection = new SqlConnection(_sql.CaseInsensitiveConnectionString))
            {
                var capabilities = new SqlServerCapabilityProvider().GetCapabilities(connection);

                Assert.False(capabilities.IsProbed);
                Assert.Same(DatabaseCapabilities.Unknown, capabilities);
            }
        }

        [SqlServerFact]
        public void A_null_connection_comes_back_unknown()
        {
            Assert.Same(
                DatabaseCapabilities.Unknown,
                new SqlServerCapabilityProvider().GetCapabilities(null));
        }

        [SqlServerFact]
        public void The_answer_is_cached_per_database()
        {
            var provider = new SqlServerCapabilityProvider();

            using (var first = _sql.OpenCaseInsensitive())
            using (var second = _sql.OpenCaseInsensitive())
            {
                var a = provider.GetCapabilities(first);
                var b = provider.GetCapabilities(second);

                Assert.Same(a, b);
            }
        }

        [SqlServerFact]
        public void Different_databases_are_probed_separately()
        {
            var provider = new SqlServerCapabilityProvider();

            using (var insensitive = _sql.OpenCaseInsensitive())
            using (var sensitive = _sql.OpenCaseSensitive())
            {
                var a = provider.GetCapabilities(insensitive);
                var b = provider.GetCapabilities(sensitive);

                Assert.NotSame(a, b);
                Assert.True(a.IsCaseInsensitive);
                Assert.False(b.IsCaseInsensitive);
            }
        }

        [SqlServerFact]
        public void An_encrypted_procedure_is_unhashable_and_therefore_never_redirected()
        {
            // WITH ENCRYPTION leaves sys.sql_modules.definition NULL. The procedure is
            // visibly present, but there is no body to compare, so the redirect has to
            // stand down rather than assume equivalence.
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var provider = new SqlServerCapabilityProvider(new[] { "netEncryptedLoad" });
                var capabilities = provider.GetCapabilities(connection);

                Assert.True(capabilities.HasProcedure("netEncryptedLoad"));
                Assert.False(capabilities.ProcedureBodyMatches("netEncryptedLoad", _sql.OriginalProcedureHash));
                Assert.False(capabilities.ProcedureBodyMatches("netEncryptedLoad", string.Empty));
            }
        }

        [SqlServerFact]
        public void A_probe_that_cannot_read_procedures_still_yields_the_other_facts()
        {
            // Procedure reading is separately guarded: losing it must cost the redirects
            // only, not the collation- and version-gated statement rewrites.
            using (var connection = _sql.OpenCaseInsensitive())
            {
                var provider = new SqlServerCapabilityProvider(new[] { "netEncryptedLoad" });
                var capabilities = provider.GetCapabilities(connection);

                Assert.True(capabilities.IsProbed);
                Assert.True(capabilities.IsCaseInsensitive);
                Assert.True(capabilities.HasIndex("IX_tblContent_Name"));
            }
        }
    }
}
