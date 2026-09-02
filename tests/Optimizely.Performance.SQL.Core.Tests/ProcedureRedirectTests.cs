using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.Tests.Fakes;

namespace Optimizely.Performance.SQL.Tests
{
    /// <summary>
    /// Redirects must withdraw themselves whenever the ground shifts: no replacement
    /// deployed, or a CMS upgrade that moved the original. The shipped procedure running
    /// is always an acceptable answer.
    /// </summary>
    public class ProcedureRedirectTests
    {
        private const string OriginalBody = "CREATE PROCEDURE dbo.netContentLoad AS SELECT 1";

        private static string BodyHash
        {
            get { return Fingerprinting.ModuleHash.Compute(OriginalBody); }
        }

        /// <summary>A database with both procedures deployed and the original unmodified.</summary>
        private static DatabaseCapabilities HealthyDatabase(string originalHash = null)
        {
            return Build.Capabilities(procedures: Build.Procedures(
                ("netContentLoad", originalHash ?? BodyHash),
                ("netContentLoad_optiperf_v1", "whatever")));
        }

        private static SqlRewriteRegistry Registry(
            ApprovedStatement redirect = null,
            RewriteOptions options = null,
            Diagnostics.IRewriteObserver observer = null)
        {
            return Build.Registry(
                Build.Document(redirect ?? Build.Redirect(originalBodyHash: BodyHash)),
                options,
                observer);
        }

        [Fact]
        public void Redirects_are_indexed_separately_from_statement_rewrites()
        {
            var registry = Registry();

            Assert.True(registry.HasProcedureRedirects);
            Assert.False(registry.IsEmpty);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void A_registry_without_redirects_says_so()
        {
            var registry = Build.Registry(Build.Document(Build.Statement()));

            Assert.False(registry.HasProcedureRedirects);
        }

        [Fact]
        public void An_incomplete_redirect_is_dropped_at_load()
        {
            var document = Build.Document(
                Build.Redirect("OPT-A", originalName: null),
                Build.Redirect("OPT-B", replacementName: null),
                new ApprovedStatement { Id = "OPT-C", Kind = RewriteKind.ProcedureRedirect, Procedure = null });

            var registry = Build.Registry(document);

            Assert.Equal(0, registry.Count);
            Assert.False(registry.HasProcedureRedirects);
        }

        [Fact]
        public void Procedure_names_of_interest_cover_originals_and_replacements()
        {
            var registry = Registry();

            Assert.Equal(
                new[] { "netContentLoad", "netContentLoad_optiperf_v1" },
                System.Linq.Enumerable.OrderBy(registry.ProcedureNames, name => name));
        }

        /// <summary>
        /// The probe only fetches what the registry asks for, so a required procedure that
        /// went unlisted would be reported missing on every database and the redirect would
        /// never fire anywhere.
        /// </summary>
        [Fact]
        public void Procedure_names_of_interest_cover_declared_dependencies()
        {
            var registry = Registry(Build.Redirect(
                originalBodyHash: BodyHash,
                preconditions: new RewritePreconditions
                {
                    RequiredProcedures = new[] { "netContentLoadHelper_optiperf_v1" }
                }));

            Assert.Contains("netContentLoadHelper_optiperf_v1", registry.ProcedureNames);
        }

        [Fact]
        public void A_healthy_database_gets_the_redirect()
        {
            var result = Registry().ResolveProcedure("netContentLoad", HealthyDatabase());

            Assert.Equal(RewriteOutcome.ProcedureRedirected, result.Reason);
            Assert.True(result.ShouldReplace);
            Assert.Equal("netContentLoad_optiperf_v1", result.Sql);
        }

        [Theory]
        [InlineData("netContentLoad")]
        [InlineData("dbo.netContentLoad")]
        [InlineData("[dbo].[netContentLoad]")]
        [InlineData("NETCONTENTLOAD")]
        public void The_call_is_matched_however_the_caller_spells_it(string spelling)
        {
            Assert.Equal(
                RewriteOutcome.ProcedureRedirected,
                Registry().ResolveProcedure(spelling, HealthyDatabase()).Reason);
        }

        [Fact]
        public void An_unrelated_procedure_is_left_alone()
        {
            Assert.Same(
                RewriteResult.NoChange,
                Registry().ResolveProcedure("netStartPageLoad", HealthyDatabase()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_blank_procedure_name_is_left_alone(string name)
        {
            Assert.Same(RewriteResult.NoChange, Registry().ResolveProcedure(name, HealthyDatabase()));
        }

        [Fact]
        public void An_undeployed_replacement_withdraws_the_redirect()
        {
            // The expected state everywhere the installer has not run. Not an error.
            var capabilities = Build.Capabilities(procedures: Build.Procedures(
                ("netContentLoad", BodyHash)));

            var result = Registry().ResolveProcedure("netContentLoad", capabilities);

            Assert.Equal(RewriteOutcome.ReplacementProcedureMissing, result.Reason);
            Assert.False(result.ShouldReplace);
        }

        [Fact]
        public void An_unprobed_database_withdraws_the_redirect()
        {
            // Without a probe we cannot tell a missing replacement from a present one.
            var result = Registry().ResolveProcedure("netContentLoad", DatabaseCapabilities.Unknown);

            Assert.Equal(RewriteOutcome.ReplacementProcedureMissing, result.Reason);
        }

        [Fact]
        public void Null_capabilities_withdraw_the_redirect()
        {
            Assert.Equal(
                RewriteOutcome.ReplacementProcedureMissing,
                Registry().ResolveProcedure("netContentLoad", null).Reason);
        }

        [Fact]
        public void A_patched_original_withdraws_the_redirect()
        {
            // A CMS upgrade has changed the shipped procedure since the replacement was
            // approved, so the replacement is no longer known to be equivalent.
            var patched = Fingerprinting.ModuleHash.Compute(OriginalBody + " -- hotfix");

            var result = Registry().ResolveProcedure("netContentLoad", HealthyDatabase(patched));

            Assert.Equal(RewriteOutcome.OriginalProcedureDrifted, result.Reason);
            Assert.False(result.ShouldReplace);
        }

        [Fact]
        public void A_redirect_with_no_recorded_hash_never_fires()
        {
            var registry = Registry(Build.Redirect(originalBodyHash: null));

            Assert.Equal(
                RewriteOutcome.OriginalProcedureDrifted,
                registry.ResolveProcedure("netContentLoad", HealthyDatabase()).Reason);
        }

        [Fact]
        public void An_unmet_precondition_withdraws_the_redirect()
        {
            var registry = Registry(Build.Redirect(
                originalBodyHash: BodyHash,
                preconditions: new RewritePreconditions { MinimumCompatibilityLevel = 150 }));

            var capabilities = Build.Capabilities(
                compatibilityLevel: 110,
                procedures: Build.Procedures(
                    ("netContentLoad", BodyHash),
                    ("netContentLoad_optiperf_v1", "whatever")));

            Assert.Equal(
                RewriteOutcome.PreconditionsNotMet,
                registry.ResolveProcedure("netContentLoad", capabilities).Reason);
        }

        [Fact]
        public void A_disabled_redirect_reports_NotActive()
        {
            var registry = Registry(Build.Redirect(originalBodyHash: BodyHash, enabled: false));

            Assert.Equal(
                RewriteOutcome.NotActive,
                registry.ResolveProcedure("netContentLoad", HealthyDatabase()).Reason);
        }

        [Fact]
        public void The_master_switch_suppresses_redirects_too()
        {
            var registry = Registry(options: new RewriteOptions { Enabled = false });

            Assert.Same(
                RewriteResult.NoChange,
                registry.ResolveProcedure("netContentLoad", HealthyDatabase()));
        }

        [Fact]
        public void Shadow_mode_reports_the_redirect_without_making_it()
        {
            var observer = new RecordingObserver();
            var registry = Registry(options: new RewriteOptions { ShadowMode = true }, observer: observer);

            var result = registry.ResolveProcedure("netContentLoad", HealthyDatabase());

            Assert.Equal(RewriteOutcome.Shadowed, result.Reason);
            Assert.False(result.ShouldReplace);
            Assert.Equal("OPT-9001", observer.Last.StatementId);
        }

        [Fact]
        public void Statement_resolution_ignores_redirect_entries()
        {
            // A redirect is keyed by procedure name, not by fingerprint, so it must never
            // be reachable from the text path.
            var registry = Registry();

            Assert.Same(RewriteResult.NoChange, registry.Resolve("netContentLoad", null, HealthyDatabase()));
        }

        /// <summary>
        /// One procedure name, several approved replacements. This is the normal case, not
        /// an edge one: CMS 11 and CMS 12 ship different bodies under the same name, and a
        /// rewrite whose benefit depends on the optimiser can need one body below a
        /// compatibility level and another above it.
        /// </summary>
        public class SeveralCandidatesForOneProcedure
        {
            private const string Cms11Body = "CREATE PROCEDURE dbo.netContentLoad AS SELECT 11";
            private const string Cms12Body = "CREATE PROCEDURE dbo.netContentLoad AS SELECT 12";

            private static string Hash(string body)
            {
                return Fingerprinting.ModuleHash.Compute(body);
            }

            private static DatabaseCapabilities Database(string deployedBody, int compatibilityLevel = 160)
            {
                return Build.Capabilities(
                    compatibilityLevel: compatibilityLevel,
                    procedures: Build.Procedures(
                        ("netContentLoad", Hash(deployedBody)),
                        ("netContentLoad_cms11", "irrelevant"),
                        ("netContentLoad_cms12", "irrelevant")));
            }

            private static ApprovedSqlDocument BothVersions()
            {
                return Build.Document(
                    Build.Redirect(
                        "OPT-CMS11",
                        replacementName: "netContentLoad_cms11",
                        originalBodyHash: Hash(Cms11Body),
                        appliesTo: CmsVersion.All),
                    Build.Redirect(
                        "OPT-CMS12",
                        replacementName: "netContentLoad_cms12",
                        originalBodyHash: Hash(Cms12Body),
                        appliesTo: CmsVersion.All));
            }

            [Fact]
            public void Both_are_kept_rather_than_the_last_one_winning()
            {
                Assert.Equal(2, Build.Registry(BothVersions()).Count);
            }

            /// <summary>
            /// The body hash is what tells the two apart, so getting this right needs no
            /// correct <c>appliesTo</c> and no correct product version -- only a database
            /// that is what it says it is.
            /// </summary>
            [Fact]
            public void The_deployed_body_selects_which_one_applies()
            {
                var registry = Build.Registry(BothVersions());

                Assert.Equal(
                    "netContentLoad_cms11",
                    registry.ResolveProcedure("netContentLoad", Database(Cms11Body)).Sql);

                Assert.Equal(
                    "netContentLoad_cms12",
                    registry.ResolveProcedure("netContentLoad", Database(Cms12Body)).Sql);
            }

            [Fact]
            public void A_body_matching_neither_candidate_leaves_the_original_running()
            {
                var result = Build.Registry(BothVersions())
                    .ResolveProcedure("netContentLoad", Database("CREATE PROCEDURE dbo.netContentLoad AS SELECT 99"));

                Assert.Equal(RewriteOutcome.OriginalProcedureDrifted, result.Reason);
                Assert.False(result.ShouldReplace);
            }

            /// <summary>
            /// The table-variable pattern: the same original, one replacement carrying
            /// recompile hints below compatibility level 150 and one without them above.
            /// A database upgrading from 140 to 150 crosses between them on the next probe,
            /// with no configuration change.
            /// </summary>
            [Theory]
            [InlineData(130, "netContentLoad_cms11")]
            [InlineData(140, "netContentLoad_cms11")]
            [InlineData(150, "netContentLoad_cms12")]
            [InlineData(170, "netContentLoad_cms12")]
            public void Disjoint_compatibility_windows_select_between_candidates(int level, string expected)
            {
                var document = Build.Document(
                    Build.Redirect(
                        "OPT-HINTED",
                        replacementName: "netContentLoad_cms11",
                        originalBodyHash: Hash(Cms11Body),
                        preconditions: new RewritePreconditions { MaximumCompatibilityLevel = 140 }),
                    Build.Redirect(
                        "OPT-PLAIN",
                        replacementName: "netContentLoad_cms12",
                        originalBodyHash: Hash(Cms11Body),
                        preconditions: new RewritePreconditions { MinimumCompatibilityLevel = 150 }));

                var result = Build.Registry(document)
                    .ResolveProcedure("netContentLoad", Database(Cms11Body, compatibilityLevel: level));

                Assert.Equal(expected, result.Sql);
            }

            /// <summary>
            /// When nothing applies, the reported reason should come from the candidate that
            /// got furthest through the gates, not from whichever happens to be listed first.
            /// Here the Commerce 15 entry fails on the body hash and the Commerce 14 entry --
            /// the one actually meant for this database -- fails on the compatibility ceiling.
            /// Reporting the hash failure would send an operator looking for a CMS patch that
            /// never happened.
            /// </summary>
            [Fact]
            public void The_reported_reason_comes_from_the_candidate_that_got_furthest()
            {
                var document = Build.Document(
                    Build.Redirect("OPT-WRONG-VERSION", replacementName: "netContentLoad_cms12", originalBodyHash: Hash(Cms12Body)),
                    Build.Redirect(
                        "OPT-RIGHT-VERSION",
                        replacementName: "netContentLoad_cms11",
                        originalBodyHash: Hash(Cms11Body),
                        preconditions: new RewritePreconditions { MaximumCompatibilityLevel = 140 }));

                var result = Build.Registry(document)
                    .ResolveProcedure("netContentLoad", Database(Cms11Body, compatibilityLevel: 160));

                Assert.Equal(RewriteOutcome.PreconditionsNotMet, result.Reason);
                Assert.Equal("OPT-RIGHT-VERSION", result.Statement.Id);
            }

            /// <summary>
            /// Order is the tie-break when preconditions overlap. Worth pinning down: it is
            /// the only thing that makes an overlapping pair deterministic.
            /// </summary>
            [Fact]
            public void Configuration_order_decides_when_more_than_one_candidate_fits()
            {
                var document = Build.Document(
                    Build.Redirect("OPT-FIRST", replacementName: "netContentLoad_cms11", originalBodyHash: Hash(Cms11Body)),
                    Build.Redirect("OPT-SECOND", replacementName: "netContentLoad_cms12", originalBodyHash: Hash(Cms11Body)));

                var result = Build.Registry(document).ResolveProcedure("netContentLoad", Database(Cms11Body));

                Assert.Equal("netContentLoad_cms11", result.Sql);
                Assert.Equal("OPT-FIRST", result.Statement.Id);
            }

            /// <summary>
            /// A candidate that is disabled or scoped to the other CMS must not block a
            /// later one that does apply.
            /// </summary>
            [Fact]
            public void An_inapplicable_candidate_does_not_shadow_a_working_one()
            {
                var document = Build.Document(
                    Build.Redirect("OPT-OFF", replacementName: "netContentLoad_cms11", originalBodyHash: Hash(Cms11Body), enabled: false),
                    Build.Redirect("OPT-ON", replacementName: "netContentLoad_cms12", originalBodyHash: Hash(Cms11Body)));

                var result = Build.Registry(document).ResolveProcedure("netContentLoad", Database(Cms11Body));

                Assert.Equal("netContentLoad_cms12", result.Sql);
            }
        }
    }
}
