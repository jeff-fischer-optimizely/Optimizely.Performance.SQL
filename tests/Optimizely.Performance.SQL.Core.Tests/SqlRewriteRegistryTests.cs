using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Fingerprinting;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.Tests.Fakes;

namespace Optimizely.Performance.SQL.Tests
{
    /// <summary>
    /// The decision engine. Everything else in the package only moves text; this is where
    /// "should we" is answered.
    /// </summary>
    public class SqlRewriteRegistryTests
    {
        private static readonly DatabaseCapabilities Modern = Build.Capabilities();

        // ---- loading -------------------------------------------------------------

        [Fact]
        public void An_empty_document_yields_an_empty_registry()
        {
            var registry = Build.Registry(Build.Document());

            Assert.True(registry.IsEmpty);
            Assert.Equal(0, registry.Count);
            Assert.False(registry.HasProcedureRedirects);
        }

        [Fact]
        public void A_null_document_yields_an_empty_registry_rather_than_throwing()
        {
            var registry = new SqlRewriteRegistry(null, new RewriteOptions());

            Assert.True(registry.IsEmpty);
        }

        [Fact]
        public void Entries_are_keyed_by_the_fingerprint_of_their_original_sql()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            Assert.Equal(1, registry.Count);

            var result = registry.Resolve(Build.OriginalSql, null, Modern);

            Assert.Equal(RewriteOutcome.Rewritten, result.Reason);
        }

        [Fact]
        public void A_stored_fingerprint_is_used_when_it_is_well_formed()
        {
            // Lets an entry key off a fingerprint the sync tool computed, even if the
            // originalSql in the file has since been reformatted.
            var statement = Build.Statement();
            statement.Fingerprint = SqlFingerprint.Compute("SELECT something else entirely");

            var registry = Build.Registry(Build.Document(statement));

            Assert.Equal(
                RewriteOutcome.Rewritten,
                registry.Resolve("SELECT something else entirely", null, Modern).Reason);

            Assert.Equal(
                RewriteOutcome.NotMatched,
                registry.Resolve(Build.OriginalSql, null, Modern).Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-fingerprint")]
        [InlineData("ABCDEF0123456789ABCDEF0123456789")]
        public void A_malformed_stored_fingerprint_is_recomputed_from_the_original_sql(string stored)
        {
            var statement = Build.Statement();
            statement.Fingerprint = stored;

            var registry = Build.Registry(Build.Document(statement));

            Assert.Equal(
                RewriteOutcome.Rewritten,
                registry.Resolve(Build.OriginalSql, null, Modern).Reason);
        }

        [Fact]
        public void Null_entries_in_the_document_are_skipped()
        {
            var registry = Build.Registry(Build.Document(null, Build.Statement(), null));

            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void Duplicate_fingerprints_resolve_to_the_last_entry()
        {
            var first = Build.Statement("OPT-0001", rewrittenSql: "SELECT 'first'");
            var second = Build.Statement("OPT-0002", rewrittenSql: "SELECT 'second'");

            var registry = Build.Registry(Build.Document(first, second));

            Assert.Equal(1, registry.Count);
            Assert.Equal("OPT-0002", registry.Resolve(Build.OriginalSql, null, Modern).Statement.Id);
        }

        // ---- matching ------------------------------------------------------------

        [Fact]
        public void An_unmatched_statement_is_left_alone()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            var result = registry.Resolve("SELECT * FROM tblSomethingElse", null, Modern);

            Assert.Same(RewriteResult.NoChange, result);
            Assert.False(result.ShouldReplace);
        }

        [Fact]
        public void Matching_ignores_formatting_differences()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            var reformatted =
                "select   c.pkID,\n       c.Name\n  from tblContent c\n -- reformatted by a developer\n where LOWER(c.Name) = LOWER(@Name)";

            Assert.Equal(RewriteOutcome.Rewritten, registry.Resolve(reformatted, null, Modern).Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Blank_command_text_is_left_alone(string commandText)
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            Assert.Same(RewriteResult.NoChange, registry.Resolve(commandText, null, Modern));
        }

        // ---- switches ------------------------------------------------------------

        [Fact]
        public void The_master_switch_suppresses_everything()
        {
            var registry = Build.Registry(
                Build.Document(Build.Statement()),
                new RewriteOptions { Enabled = false });

            Assert.Same(RewriteResult.NoChange, registry.Resolve(Build.OriginalSql, null, Modern));
        }

        [Fact]
        public void A_disabled_entry_reports_NotActive()
        {
            var registry = Build.Registry(Build.Document(Build.Statement(enabled: false)));

            var result = registry.Resolve(Build.OriginalSql, null, Modern);

            Assert.Equal(RewriteOutcome.NotActive, result.Reason);
            Assert.False(result.ShouldReplace);
        }

        [Fact]
        public void An_entry_for_another_cms_version_reports_NotActive()
        {
            var registry = Build.Registry(
                Build.Document(Build.Statement(appliesTo: CmsVersion.V11)),
                new RewriteOptions { Version = CmsVersion.V12 });

            Assert.Equal(RewriteOutcome.NotActive, registry.Resolve(Build.OriginalSql, null, Modern).Reason);
        }

        [Fact]
        public void An_entry_for_all_versions_fires_on_either()
        {
            foreach (var version in new[] { CmsVersion.V11, CmsVersion.V12 })
            {
                var registry = Build.Registry(
                    Build.Document(Build.Statement(appliesTo: CmsVersion.All)),
                    new RewriteOptions { Version = version });

                Assert.Equal(RewriteOutcome.Rewritten, registry.Resolve(Build.OriginalSql, null, Modern).Reason);
            }
        }

        [Fact]
        public void Active_count_reflects_the_configured_version()
        {
            var document = Build.Document(
                Build.Statement("OPT-0001", appliesTo: CmsVersion.V11),
                Build.Statement("OPT-0002", originalSql: "SELECT 2", appliesTo: CmsVersion.V12),
                Build.Statement("OPT-0003", originalSql: "SELECT 3", enabled: false));

            var registry = Build.Registry(document, new RewriteOptions { Version = CmsVersion.V12 });

            Assert.Equal(3, registry.Count);
            Assert.Equal(1, registry.ActiveCount);
        }

        // ---- preconditions -------------------------------------------------------

        [Fact]
        public void An_unmet_precondition_reports_PreconditionsNotMet_and_changes_nothing()
        {
            var statement = Build.Statement(
                preconditions: new RewritePreconditions { RequiresCaseInsensitiveCollation = true });

            var registry = Build.Registry(Build.Document(statement));

            var result = registry.Resolve(
                Build.OriginalSql,
                null,
                Build.Capabilities("SQL_Latin1_General_CP1_CS_AS"));

            Assert.Equal(RewriteOutcome.PreconditionsNotMet, result.Reason);
            Assert.False(result.ShouldReplace);
            Assert.Null(result.Sql);
        }

        [Fact]
        public void Null_capabilities_are_treated_as_unprobed()
        {
            var statement = Build.Statement(
                preconditions: new RewritePreconditions { RequiresCaseInsensitiveCollation = true });

            var registry = Build.Registry(Build.Document(statement));

            Assert.Equal(
                RewriteOutcome.PreconditionsNotMet,
                registry.Resolve(Build.OriginalSql, null, null).Reason);
        }

        [Fact]
        public void An_unconditional_rewrite_fires_even_unprobed()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            Assert.Equal(
                RewriteOutcome.Rewritten,
                registry.Resolve(Build.OriginalSql, null, DatabaseCapabilities.Unknown).Reason);
        }

        // ---- variants ------------------------------------------------------------

        private static ApprovedStatement VariantStatement()
        {
            return Build.Statement(
                id: "OPT-0007",
                originalSql: "SELECT * FROM tblContentLanguage l WHERE (@LanguageBranch IS NULL OR l.Name = @LanguageBranch)",
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
                        Sql = "SELECT * FROM tblContentLanguage l",
                        DropsParameters = new[] { "LanguageBranch" }
                    },
                    new StatementVariant
                    {
                        Id = "language",
                        When = new[]
                        {
                            new VariantCondition { Parameter = "LanguageBranch", Test = ParameterTest.IsNotNull }
                        },
                        Sql = "SELECT * FROM tblContentLanguage l WHERE l.Name = @LanguageBranch"
                    }
                });
        }

        [Fact]
        public void A_null_parameter_selects_the_predicate_free_variant()
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(
                VariantStatement().OriginalSql,
                Build.Parameters(("@LanguageBranch", System.DBNull.Value)),
                Modern);

            Assert.Equal(RewriteOutcome.Rewritten, result.Reason);
            Assert.Equal("no-language", result.Variant.Id);
            Assert.Equal("SELECT * FROM tblContentLanguage l", result.Sql);
            Assert.Equal(new[] { "LanguageBranch" }, result.DroppedParameters);
        }

        [Fact]
        public void A_supplied_parameter_selects_the_seeking_variant()
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(
                VariantStatement().OriginalSql,
                Build.Parameters(("@LanguageBranch", "en")),
                Modern);

            Assert.Equal("language", result.Variant.Id);
            Assert.Null(result.DroppedParameters);
        }

        [Fact]
        public void An_absent_parameter_counts_as_null()
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(VariantStatement().OriginalSql, Build.Parameters(), Modern);

            Assert.Equal("no-language", result.Variant.Id);
        }

        [Fact]
        public void A_clr_null_value_counts_as_null()
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(
                VariantStatement().OriginalSql,
                Build.Parameters(("@LanguageBranch", null)),
                Modern);

            Assert.Equal("no-language", result.Variant.Id);
        }

        [Theory]
        [InlineData("@LanguageBranch")]
        [InlineData("LanguageBranch")]
        [InlineData("languagebranch")]
        [InlineData(":LanguageBranch")]
        public void Parameter_names_match_with_or_without_a_sigil(string suppliedName)
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(
                VariantStatement().OriginalSql,
                Build.Parameters((suppliedName, "en")),
                Modern);

            Assert.Equal("language", result.Variant.Id);
        }

        [Fact]
        public void Variants_are_evaluated_in_declaration_order()
        {
            var statement = Build.Statement(
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant { Id = "catch-all", Sql = "SELECT 'first'" },
                    new StatementVariant { Id = "never-reached", Sql = "SELECT 'second'" }
                });

            var registry = Build.Registry(
                Build.Document(statement),
                new RewriteOptions { AnnotateRewrittenSql = false });

            Assert.Equal("catch-all", registry.Resolve(Build.OriginalSql, null, Modern).Variant.Id);
        }

        [Fact]
        public void Every_condition_on_a_variant_must_hold()
        {
            var statement = Build.Statement(
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "both",
                        When = new[]
                        {
                            new VariantCondition { Parameter = "a", Test = ParameterTest.IsNotNull },
                            new VariantCondition { Parameter = "b", Test = ParameterTest.IsNotNull }
                        },
                        Sql = "SELECT 'both'"
                    }
                });

            var registry = Build.Registry(Build.Document(statement));

            Assert.Equal(
                RewriteOutcome.NoVariantMatched,
                registry.Resolve(Build.OriginalSql, Build.Parameters(("a", 1)), Modern).Reason);

            Assert.Equal(
                RewriteOutcome.Rewritten,
                registry.Resolve(Build.OriginalSql, Build.Parameters(("a", 1), ("b", 2)), Modern).Reason);
        }

        [Fact]
        public void No_matching_variant_and_no_default_reports_NoVariantMatched()
        {
            var statement = Build.Statement(
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "needs-x",
                        When = new[] { new VariantCondition { Parameter = "x", Test = ParameterTest.IsPresent } },
                        Sql = "SELECT 'x'"
                    }
                });

            var registry = Build.Registry(Build.Document(statement));
            var result = registry.Resolve(Build.OriginalSql, Build.Parameters(), Modern);

            Assert.Equal(RewriteOutcome.NoVariantMatched, result.Reason);
            Assert.False(result.ShouldReplace);
        }

        [Fact]
        public void No_matching_variant_falls_back_to_the_default_rewrite()
        {
            var statement = Build.Statement(
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "needs-x",
                        When = new[] { new VariantCondition { Parameter = "x", Test = ParameterTest.IsPresent } },
                        Sql = "SELECT 'x'"
                    }
                });

            var registry = Build.Registry(
                Build.Document(statement),
                new RewriteOptions { AnnotateRewrittenSql = false });

            var result = registry.Resolve(Build.OriginalSql, Build.Parameters(), Modern);

            Assert.Equal(RewriteOutcome.Rewritten, result.Reason);
            Assert.Null(result.Variant);
            Assert.Equal(Build.RewrittenSql, result.Sql);
        }

        [Fact]
        public void A_variant_with_no_sql_is_skipped()
        {
            var statement = Build.Statement(
                variants: new[]
                {
                    new StatementVariant { Id = "empty", Sql = null },
                    new StatementVariant { Id = "real", Sql = "SELECT 'real'" }
                });

            var registry = Build.Registry(
                Build.Document(statement),
                new RewriteOptions { AnnotateRewrittenSql = false });

            Assert.Equal("real", registry.Resolve(Build.OriginalSql, null, Modern).Variant.Id);
        }

        [Theory]
        [InlineData(ParameterTest.IsPresent, "value", true)]
        [InlineData(ParameterTest.IsPresent, null, true)]
        [InlineData(ParameterTest.IsAbsent, "value", false)]
        public void Presence_tests_ignore_the_value(ParameterTest test, object value, bool expected)
        {
            Assert.Equal(expected, Selects(test, null, Build.Parameters(("p", value))));
        }

        [Fact]
        public void IsAbsent_holds_only_when_the_parameter_is_not_supplied()
        {
            Assert.True(Selects(ParameterTest.IsAbsent, null, Build.Parameters()));
            Assert.False(Selects(ParameterTest.IsAbsent, null, Build.Parameters(("p", null))));
        }

        [Theory]
        [InlineData("en", "en", true)]
        [InlineData("EN", "en", true)]
        [InlineData("sv", "en", false)]
        [InlineData(42, "42", true)]
        public void EqualsValue_compares_the_invariant_string_form(object actual, string expected, bool matches)
        {
            Assert.Equal(matches, Selects(ParameterTest.EqualsValue, expected, Build.Parameters(("p", actual))));
        }

        [Fact]
        public void EqualsValue_against_a_null_parameter_matches_only_a_null_operand()
        {
            Assert.True(Selects(ParameterTest.EqualsValue, null, Build.Parameters(("p", System.DBNull.Value))));
            Assert.False(Selects(ParameterTest.EqualsValue, "en", Build.Parameters(("p", System.DBNull.Value))));
        }

        [Fact]
        public void A_condition_naming_no_parameter_is_ignored()
        {
            var statement = Build.Statement(
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "v",
                        When = new[] { new VariantCondition { Parameter = null, Test = ParameterTest.IsNotNull } },
                        Sql = "SELECT 'v'"
                    }
                });

            var registry = Build.Registry(Build.Document(statement));

            Assert.Equal(RewriteOutcome.Rewritten, registry.Resolve(Build.OriginalSql, null, Modern).Reason);
        }

        /// <summary>True when a single-condition variant is selected for the given parameters.</summary>
        private static bool Selects(ParameterTest test, string value, System.Data.Common.DbParameterCollection parameters)
        {
            var statement = Build.Statement(
                rewrittenSql: null,
                variants: new[]
                {
                    new StatementVariant
                    {
                        Id = "v",
                        When = new[] { new VariantCondition { Parameter = "p", Test = test, Value = value } },
                        Sql = "SELECT 'v'"
                    }
                });

            var registry = Build.Registry(Build.Document(statement));

            return registry.Resolve(Build.OriginalSql, parameters, Modern).Reason == RewriteOutcome.Rewritten;
        }

        // ---- shadow mode and annotation -----------------------------------------

        [Fact]
        public void Shadow_mode_resolves_fully_but_substitutes_nothing()
        {
            var observer = new RecordingObserver();

            var registry = Build.Registry(
                Build.Document(Build.Statement()),
                new RewriteOptions { ShadowMode = true },
                observer);

            var result = registry.Resolve(Build.OriginalSql, null, Modern);

            Assert.Equal(RewriteOutcome.Shadowed, result.Reason);
            Assert.False(result.ShouldReplace);
            Assert.Null(result.Sql);
            Assert.Equal("OPT-0001", observer.Last.StatementId);
        }

        [Fact]
        public void Annotation_prefixes_the_rewrite_id_as_a_comment()
        {
            var registry = Build.Registry(
                Build.Document(Build.Statement()),
                new RewriteOptions { AnnotateRewrittenSql = true });

            var sql = registry.Resolve(Build.OriginalSql, null, Modern).Sql;

            Assert.StartsWith("/* opti-perf-sql:OPT-0001 */", sql);
            Assert.Contains(Build.RewrittenSql, sql);
        }

        [Fact]
        public void Annotation_names_the_variant_when_one_was_chosen()
        {
            var registry = Build.Registry(
                Build.Document(VariantStatement()),
                new RewriteOptions { AnnotateRewrittenSql = true });

            var sql = registry
                .Resolve(VariantStatement().OriginalSql, Build.Parameters(("@LanguageBranch", "en")), Modern)
                .Sql;

            Assert.StartsWith("/* opti-perf-sql:OPT-0007/language */", sql);
        }

        [Fact]
        public void An_annotated_rewrite_still_fingerprints_back_to_the_same_entry()
        {
            // The annotation is a comment, and the normalizer strips comments — so the
            // marker cannot cause the rewritten text to miss its own entry if it somehow
            // came round again.
            var registry = Build.Registry(Build.Document(Build.Statement()));

            var annotated = registry.Resolve(Build.OriginalSql, null, Modern).Sql;

            Assert.Equal(
                SqlFingerprint.Compute(Build.RewrittenSql),
                SqlFingerprint.Compute(annotated));
        }

        [Fact]
        public void Annotation_can_be_switched_off()
        {
            var registry = Build.Registry(
                Build.Document(Build.Statement()),
                new RewriteOptions { AnnotateRewrittenSql = false });

            Assert.Equal(Build.RewrittenSql, registry.Resolve(Build.OriginalSql, null, Modern).Sql);
        }

        // ---- observers -----------------------------------------------------------

        [Fact]
        public void An_unmatched_statement_is_not_reported()
        {
            var observer = new RecordingObserver();
            var registry = Build.Registry(Build.Document(Build.Statement()), null, observer);

            registry.Resolve("SELECT * FROM tblSomethingElse", null, Modern);

            Assert.Empty(observer.Events);
        }

        [Fact]
        public void Every_matched_outcome_is_reported_including_the_skips()
        {
            var observer = new RecordingObserver();

            var statement = Build.Statement(
                preconditions: new RewritePreconditions { MinimumCompatibilityLevel = 150 });

            var registry = Build.Registry(Build.Document(statement), null, observer);

            registry.Resolve(Build.OriginalSql, null, Build.Capabilities(compatibilityLevel: 110));

            var reported = Assert.Single(observer.Events);
            Assert.Equal(RewriteOutcome.PreconditionsNotMet, reported.Outcome);
            Assert.Equal("OPT-0001", reported.StatementId);
            Assert.Equal(SqlFingerprint.Compute(Build.OriginalSql), reported.Fingerprint);
            Assert.Equal(Build.OriginalSql, reported.OriginalSql);
            Assert.Null(reported.RewrittenSql);
        }

        [Fact]
        public void A_broken_observer_cannot_break_the_rewrite()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()), null, new ThrowingObserver());

            var result = registry.Resolve(Build.OriginalSql, null, Modern);

            Assert.Equal(RewriteOutcome.Rewritten, result.Reason);
        }

        [Fact]
        public void A_composite_observer_keeps_going_past_a_broken_one()
        {
            var good = new RecordingObserver();

            var registry = Build.Registry(
                Build.Document(Build.Statement()),
                null,
                new Diagnostics.CompositeRewriteObserver(new ThrowingObserver(), good));

            registry.Resolve(Build.OriginalSql, null, Modern);

            Assert.Single(good.Events);
        }

        // ---- display -------------------------------------------------------------

        [Fact]
        public void An_unmatched_result_displays_as_none()
        {
            Assert.Equal("(none)", RewriteResult.NoChange.DisplayId);
        }
    }
}
