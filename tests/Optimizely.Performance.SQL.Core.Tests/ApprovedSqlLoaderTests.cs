using System;
using System.IO;
using System.Text.Json;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;
using Optimizely.Performance.SQL.Tests.Fakes;

namespace Optimizely.Performance.SQL.Tests
{
    public class ApprovedSqlLoaderTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "optiperf-tests-" + Guid.NewGuid().ToString("n"));

        public ApprovedSqlLoaderTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private string WriteFile(string name, string contents)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, contents);
            return path;
        }

        // ---- missing and malformed ----------------------------------------------

        [Fact]
        public void A_missing_file_yields_an_empty_document()
        {
            // A site that has installed the package but approved nothing must start.
            var document = ApprovedSqlLoader.Load(Path.Combine(_directory, "absent.json"));

            Assert.NotNull(document);
            Assert.Empty(document.Statements);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_path_yields_an_empty_document(string path)
        {
            Assert.Empty(ApprovedSqlLoader.Load(path).Statements);
        }

        [Fact]
        public void A_malformed_file_throws_rather_than_running_unrewritten()
        {
            var path = WriteFile("bad.json", "{ this is not json");

            Assert.Throws<JsonException>(() => ApprovedSqlLoader.Load(path));
        }

        [Fact]
        public void TryLoad_reports_a_malformed_file_instead_of_throwing()
        {
            // Hot reload path: an operator editing a file must not take the site down.
            var path = WriteFile("bad.json", "{ this is not json");

            var ok = ApprovedSqlLoader.TryLoad(path, out var document, out var error);

            Assert.False(ok);
            Assert.Empty(document.Statements);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void TryLoad_succeeds_on_a_good_file()
        {
            var path = WriteFile("good.json", ApprovedSqlLoader.Serialize(
                Build.Document(Build.Statement())));

            var ok = ApprovedSqlLoader.TryLoad(path, out var document, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Single(document.Statements);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_json_parses_to_an_empty_document(string json)
        {
            Assert.Empty(ApprovedSqlLoader.Parse(json).Statements);
        }

        [Fact]
        public void A_json_null_parses_to_an_empty_document()
        {
            Assert.Empty(ApprovedSqlLoader.Parse("null").Statements);
        }

        [Fact]
        public void A_document_with_no_statements_array_parses_to_an_empty_one()
        {
            Assert.Empty(ApprovedSqlLoader.Parse("{ \"schemaVersion\": 1 }").Statements);
        }

        // ---- the real file shape -------------------------------------------------

        private const string SampleJson = @"{
  // Generated file: comments and trailing commas are tolerated so the sync tool can
  // annotate its output.
  ""schemaVersion"": 1,
  ""generatedBy"": ""sync-tool"",
  ""statements"": [
    {
      ""id"": ""OPT-0007"",
      ""title"": ""Content children by language"",
      ""kind"": ""StatementRewrite"",
      ""appliesTo"": ""V12"",
      ""approvedBy"": ""a.reviewer"",
      ""approvedOn"": ""2026-01-15"",
      ""approvalDocument"": ""approvals/OPT-0007-content-children.md"",
      ""originalSql"": ""SELECT * FROM tblContent WHERE (@LanguageBranch IS NULL OR Name = @LanguageBranch)"",
      ""variants"": [
        {
          ""id"": ""no-language"",
          ""when"": [ { ""parameter"": ""LanguageBranch"", ""test"": ""IsNull"" } ],
          ""sql"": ""SELECT * FROM tblContent"",
          ""dropsParameters"": [ ""LanguageBranch"" ]
        },
      ],
      ""preconditions"": {
        ""requiresCaseInsensitiveCollation"": true,
        ""minimumCompatibilityLevel"": 130,
        ""requiredIndexes"": [ ""IX_tblContent_Name"" ]
      },
      ""recommendedIndexes"": [ ""CREATE INDEX IX_tblContent_Name ON tblContent (Name)"" ],
      ""notes"": ""Scan to seek."",
      ""enabled"": true
    },
    {
      ""id"": ""OPT-9001"",
      ""kind"": ""ProcedureRedirect"",
      ""procedure"": {
        ""originalName"": ""netContentLoad"",
        ""replacementName"": ""netContentLoad_optiperf_v1"",
        ""originalBodyHash"": ""0000000000000000000000000000000000000000000000000000000000000000""
      }
    }
  ]
}";

        [Fact]
        public void A_generated_document_parses_with_comments_and_trailing_commas()
        {
            var document = ApprovedSqlLoader.Parse(SampleJson);

            Assert.Equal(1, document.SchemaVersion);
            Assert.Equal("sync-tool", document.GeneratedBy);
            Assert.Equal(2, document.Statements.Length);
        }

        [Fact]
        public void Enums_are_read_from_their_names()
        {
            var document = ApprovedSqlLoader.Parse(SampleJson);

            Assert.Equal(RewriteKind.StatementRewrite, document.Statements[0].Kind);
            Assert.Equal(CmsVersion.V12, document.Statements[0].AppliesTo);
            Assert.Equal(ParameterTest.IsNull, document.Statements[0].Variants[0].When[0].Test);
            Assert.Equal(RewriteKind.ProcedureRedirect, document.Statements[1].Kind);
        }

        [Fact]
        public void Preconditions_and_provenance_survive_the_round_trip_from_disk()
        {
            var statement = ApprovedSqlLoader.Parse(SampleJson).Statements[0];

            Assert.True(statement.Preconditions.RequiresCaseInsensitiveCollation);
            Assert.Equal(130, statement.Preconditions.MinimumCompatibilityLevel);
            Assert.Null(statement.Preconditions.MinimumSqlServerMajorVersion);
            Assert.Equal(new[] { "IX_tblContent_Name" }, statement.Preconditions.RequiredIndexes);
            Assert.Equal("a.reviewer", statement.ApprovedBy);
            Assert.Equal("approvals/OPT-0007-content-children.md", statement.ApprovalDocument);
        }

        [Fact]
        public void An_omitted_kind_defaults_to_a_statement_rewrite()
        {
            var document = ApprovedSqlLoader.Parse(
                "{ \"statements\": [ { \"id\": \"X\", \"originalSql\": \"SELECT 1\", \"rewrittenSql\": \"SELECT 2\" } ] }");

            Assert.Equal(RewriteKind.StatementRewrite, document.Statements[0].Kind);
            Assert.Equal(CmsVersion.All, document.Statements[0].AppliesTo);
            Assert.True(document.Statements[0].Enabled);
        }

        [Fact]
        public void A_document_loaded_from_disk_drives_the_registry()
        {
            // The end-to-end configuration path: file on disk to a live decision.
            var path = WriteFile("approved-sql.json", SampleJson);

            var document = ApprovedSqlLoader.Load(path);
            var registry = Build.Registry(document, new RewriteOptions
            {
                Version = CmsVersion.V12,
                AnnotateRewrittenSql = false
            });

            Assert.Equal(2, registry.Count);
            Assert.True(registry.HasProcedureRedirects);

            var result = registry.Resolve(
                "SELECT * FROM tblContent WHERE (@LanguageBranch IS NULL OR Name = @LanguageBranch)",
                Build.Parameters(("@LanguageBranch", DBNull.Value)),
                Build.Capabilities(indexes: new[] { "IX_tblContent_Name" }));

            Assert.Equal(RewriteOutcome.Rewritten, result.Reason);
            Assert.Equal("SELECT * FROM tblContent", result.Sql);
        }

        [Fact]
        public void The_same_document_is_inert_against_a_database_missing_the_index()
        {
            var registry = Build.Registry(
                ApprovedSqlLoader.Parse(SampleJson),
                new RewriteOptions { Version = CmsVersion.V12 });

            var result = registry.Resolve(
                "SELECT * FROM tblContent WHERE (@LanguageBranch IS NULL OR Name = @LanguageBranch)",
                Build.Parameters(("@LanguageBranch", DBNull.Value)),
                Build.Capabilities());

            Assert.Equal(RewriteOutcome.PreconditionsNotMet, result.Reason);
        }

        // ---- serialization -------------------------------------------------------

        [Fact]
        public void Serialize_and_parse_round_trip()
        {
            var original = Build.Document(
                Build.Statement(preconditions: new RewritePreconditions { MinimumCompatibilityLevel = 150 }),
                Build.Redirect(originalBodyHash: "abc"));

            var reloaded = ApprovedSqlLoader.Parse(ApprovedSqlLoader.Serialize(original));

            Assert.Equal(2, reloaded.Statements.Length);
            Assert.Equal("OPT-0001", reloaded.Statements[0].Id);
            Assert.Equal(150, reloaded.Statements[0].Preconditions.MinimumCompatibilityLevel);
            Assert.Equal(RewriteKind.ProcedureRedirect, reloaded.Statements[1].Kind);
            Assert.Equal("abc", reloaded.Statements[1].Procedure.OriginalBodyHash);
        }

        [Fact]
        public void Serialization_writes_enum_names_not_numbers()
        {
            var json = ApprovedSqlLoader.Serialize(Build.Document(Build.Redirect()));

            Assert.Contains("\"ProcedureRedirect\"", json);
            Assert.DoesNotContain("\"kind\": 1", json);
        }

        [Fact]
        public void Serialization_omits_nulls()
        {
            var json = ApprovedSqlLoader.Serialize(Build.Document(Build.Statement()));

            Assert.DoesNotContain("\"notes\"", json);
            Assert.DoesNotContain("\"procedure\"", json);
        }

        // ---- path resolution -----------------------------------------------------

        [Fact]
        public void A_relative_path_resolves_against_the_supplied_base_directory()
        {
            var resolved = ApprovedSqlLoader.Resolve("config/approved-sql.json", _directory);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(_directory, "config", "approved-sql.json")),
                resolved);
        }

        [Fact]
        public void An_absolute_path_is_left_alone()
        {
            var absolute = Path.Combine(_directory, "approved-sql.json");

            Assert.Equal(absolute, ApprovedSqlLoader.Resolve(absolute, "C:\\somewhere\\else"));
        }

        [Fact]
        public void A_relative_path_defaults_to_the_application_base_directory()
        {
            var resolved = ApprovedSqlLoader.Resolve("config/approved-sql.json");

            Assert.StartsWith(AppDomain.CurrentDomain.BaseDirectory, resolved);
        }

        [Fact]
        public void The_default_configuration_path_matches_the_documented_one()
        {
            Assert.Equal("config/approved-sql.json", new RewriteOptions().ConfigurationPath);
        }
    }
}
