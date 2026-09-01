using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.Tests.Fakes;

namespace Optimizely.Performance.SQL.Tests
{
    /// <summary>
    /// Preconditions are the safety interlock: they are what stands between "this rewrite
    /// is faster" and "this rewrite returns different rows". Every one of them must fail
    /// closed.
    /// </summary>
    public class DatabaseCapabilitiesTests
    {
        [Fact]
        public void Unknown_reports_nothing_probed()
        {
            Assert.False(DatabaseCapabilities.Unknown.IsProbed);
            Assert.False(DatabaseCapabilities.Unknown.IsCaseInsensitive);
            Assert.False(DatabaseCapabilities.Unknown.HasIndex("IX_anything"));
            Assert.False(DatabaseCapabilities.Unknown.HasProcedure("netContentLoad"));
        }

        [Theory]
        [InlineData("SQL_Latin1_General_CP1_CI_AS", true)]
        [InlineData("Latin1_General_100_CI_AI_SC_UTF8", true)]
        [InlineData("SQL_Latin1_General_CP1_CS_AS", false)]
        [InlineData("Finnish_Swedish_CS_AS", false)]
        [InlineData("", false)]
        public void Case_insensitivity_is_read_from_the_collation_name(string collation, bool expected)
        {
            Assert.Equal(expected, Build.Capabilities(collation: collation).IsCaseInsensitive);
        }

        [Theory]
        [InlineData("SQL_Latin1_General_CP1_CI_AS", false)]
        [InlineData("Latin1_General_100_CI_AI", true)]
        [InlineData("Latin1_General_100_CI_AI_SC_UTF8", true)]
        public void Accent_insensitivity_is_read_from_the_collation_name(string collation, bool expected)
        {
            Assert.Equal(expected, Build.Capabilities(collation: collation).IsAccentInsensitive);
        }

        [Fact]
        public void An_unconditional_rewrite_applies_even_against_an_unprobed_database()
        {
            Assert.True(DatabaseCapabilities.Unknown.Satisfies(null));
            Assert.True(DatabaseCapabilities.Unknown.Satisfies(new RewritePreconditions()));
        }

        [Fact]
        public void Any_precondition_at_all_fails_against_an_unprobed_database()
        {
            // Probing off, or a probe that failed, must degrade toward doing nothing.
            var preconditions = new RewritePreconditions { RequiresCaseInsensitiveCollation = true };

            Assert.False(DatabaseCapabilities.Unknown.Satisfies(preconditions));
        }

        [Fact]
        public void Case_insensitive_precondition_blocks_a_case_sensitive_database()
        {
            var preconditions = new RewritePreconditions { RequiresCaseInsensitiveCollation = true };

            Assert.True(Build.Capabilities("SQL_Latin1_General_CP1_CI_AS").Satisfies(preconditions));
            Assert.False(Build.Capabilities("SQL_Latin1_General_CP1_CS_AS").Satisfies(preconditions));
        }

        [Fact]
        public void Accent_insensitive_precondition_blocks_an_accent_sensitive_database()
        {
            var preconditions = new RewritePreconditions { RequiresAccentInsensitiveCollation = true };

            Assert.True(Build.Capabilities("Latin1_General_100_CI_AI").Satisfies(preconditions));
            Assert.False(Build.Capabilities("SQL_Latin1_General_CP1_CI_AS").Satisfies(preconditions));
        }

        [Theory]
        [InlineData(150, 160, true)]
        [InlineData(150, 150, true)]
        [InlineData(150, 110, false)]
        [InlineData(150, 0, false)]
        public void Compatibility_level_floor_is_inclusive(int required, int actual, bool expected)
        {
            var preconditions = new RewritePreconditions { MinimumCompatibilityLevel = required };

            Assert.Equal(expected, Build.Capabilities(compatibilityLevel: actual).Satisfies(preconditions));
        }

        [Fact]
        public void Compatibility_level_is_checked_independently_of_product_version()
        {
            // The estate case this exists for: a current engine serving 2008-era plans.
            // Product version alone would wave this through.
            var capabilities = Build.Capabilities(majorVersion: 16, compatibilityLevel: 100);

            Assert.True(capabilities.Satisfies(new RewritePreconditions { MinimumSqlServerMajorVersion = 11 }));
            Assert.False(capabilities.Satisfies(new RewritePreconditions { MinimumCompatibilityLevel = 150 }));
        }

        [Theory]
        [InlineData(11, 16, true)]
        [InlineData(11, 11, true)]
        [InlineData(11, 10, false)]
        public void Product_version_floor_is_inclusive(int required, int actual, bool expected)
        {
            var preconditions = new RewritePreconditions { MinimumSqlServerMajorVersion = required };

            Assert.Equal(expected, Build.Capabilities(majorVersion: actual).Satisfies(preconditions));
        }

        [Fact]
        public void Every_required_index_must_be_present()
        {
            var capabilities = Build.Capabilities(indexes: new[] { "IX_tblContent_Name", "IX_tblContent_Parent" });

            Assert.True(capabilities.Satisfies(new RewritePreconditions
            {
                RequiredIndexes = new[] { "IX_tblContent_Name" }
            }));

            Assert.True(capabilities.Satisfies(new RewritePreconditions
            {
                RequiredIndexes = new[] { "IX_tblContent_Name", "IX_tblContent_Parent" }
            }));

            Assert.False(capabilities.Satisfies(new RewritePreconditions
            {
                RequiredIndexes = new[] { "IX_tblContent_Name", "IX_missing" }
            }));
        }

        [Fact]
        public void Index_names_are_matched_case_insensitively()
        {
            var capabilities = Build.Capabilities(indexes: new[] { "IX_tblContent_Name" });

            Assert.True(capabilities.HasIndex("ix_tblcontent_name"));
        }

        [Fact]
        public void All_preconditions_must_hold_together()
        {
            var preconditions = new RewritePreconditions
            {
                RequiresCaseInsensitiveCollation = true,
                MinimumCompatibilityLevel = 150,
                RequiredIndexes = new[] { "IX_tblContent_Name" }
            };

            Assert.True(Build
                .Capabilities("SQL_Latin1_General_CP1_CI_AS", 16, 160, new[] { "IX_tblContent_Name" })
                .Satisfies(preconditions));

            // One failure anywhere is enough.
            Assert.False(Build
                .Capabilities("SQL_Latin1_General_CP1_CI_AS", 16, 140, new[] { "IX_tblContent_Name" })
                .Satisfies(preconditions));

            Assert.False(Build
                .Capabilities("SQL_Latin1_General_CP1_CI_AS", 16, 160, new string[0])
                .Satisfies(preconditions));
        }

        [Theory]
        [InlineData("netContentLoad")]
        [InlineData("dbo.netContentLoad")]
        [InlineData("[dbo].[netContentLoad]")]
        [InlineData("  [MyDb].[dbo].[netContentLoad]  ")]
        [InlineData("NETCONTENTLOAD")]
        public void Procedure_names_are_matched_regardless_of_schema_and_quoting(string spelling)
        {
            var capabilities = Build.Capabilities(
                procedures: Build.Procedures(("netContentLoad", "abc123")));

            Assert.True(capabilities.HasProcedure(spelling));
            Assert.True(capabilities.ProcedureBodyMatches(spelling, "abc123"));
        }

        [Fact]
        public void A_body_hash_mismatch_reads_as_drift()
        {
            var capabilities = Build.Capabilities(
                procedures: Build.Procedures(("netContentLoad", "abc123")));

            Assert.False(capabilities.ProcedureBodyMatches("netContentLoad", "def456"));
        }

        [Fact]
        public void An_absent_expected_hash_never_matches()
        {
            // Without a hash we cannot show the original is what the replacement was
            // written against, so the answer has to be no.
            var capabilities = Build.Capabilities(
                procedures: Build.Procedures(("netContentLoad", "abc123")));

            Assert.False(capabilities.ProcedureBodyMatches("netContentLoad", null));
            Assert.False(capabilities.ProcedureBodyMatches("netContentLoad", string.Empty));
        }

        [Fact]
        public void An_unknown_procedure_never_matches()
        {
            var capabilities = Build.Capabilities(
                procedures: Build.Procedures(("netContentLoad", "abc123")));

            Assert.False(capabilities.HasProcedure("somethingElse"));
            Assert.False(capabilities.ProcedureBodyMatches("somethingElse", "abc123"));
        }
    }
}
