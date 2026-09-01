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
    }
}
