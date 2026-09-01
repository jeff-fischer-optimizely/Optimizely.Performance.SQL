using System.Collections.Generic;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Rewriting;

namespace Optimizely.Performance.SQL.Tests.Fakes
{
    /// <summary>
    /// Terse constructors for the configuration objects the tests need. Approved-SQL
    /// documents are verbose by design, and spelling one out in every test buries the
    /// thing under assertion.
    /// </summary>
    public static class Build
    {
        /// <summary>A representative CMS statement: a case-folded predicate that cannot seek.</summary>
        public const string OriginalSql =
            "SELECT c.pkID, c.Name FROM tblContent c WHERE LOWER(c.Name) = LOWER(@Name)";

        /// <summary>Its approved replacement, relying on a case-insensitive collation.</summary>
        public const string RewrittenSql =
            "SELECT c.pkID, c.Name FROM tblContent c WHERE c.Name = @Name";

        public static ApprovedStatement Statement(
            string id = "OPT-0001",
            string originalSql = OriginalSql,
            string rewrittenSql = RewrittenSql,
            RewritePreconditions preconditions = null,
            StatementVariant[] variants = null,
            CmsVersion appliesTo = CmsVersion.All,
            bool enabled = true)
        {
            return new ApprovedStatement
            {
                Id = id,
                Title = id + " test statement",
                Kind = RewriteKind.StatementRewrite,
                OriginalSql = originalSql,
                RewrittenSql = rewrittenSql,
                Preconditions = preconditions,
                Variants = variants,
                AppliesTo = appliesTo,
                Enabled = enabled
            };
        }

        public static ApprovedStatement Redirect(
            string id = "OPT-9001",
            string originalName = "netContentLoad",
            string replacementName = "netContentLoad_optiperf_v1",
            string originalBodyHash = null,
            RewritePreconditions preconditions = null,
            CmsVersion appliesTo = CmsVersion.All,
            bool enabled = true)
        {
            return new ApprovedStatement
            {
                Id = id,
                Title = id + " test redirect",
                Kind = RewriteKind.ProcedureRedirect,
                Procedure = new ProcedureRedirect
                {
                    OriginalName = originalName,
                    ReplacementName = replacementName,
                    OriginalBodyHash = originalBodyHash
                },
                Preconditions = preconditions,
                AppliesTo = appliesTo,
                Enabled = enabled
            };
        }

        public static ApprovedSqlDocument Document(params ApprovedStatement[] statements)
        {
            return new ApprovedSqlDocument
            {
                SchemaVersion = 1,
                GeneratedBy = "tests",
                Statements = statements
            };
        }

        public static SqlRewriteRegistry Registry(
            ApprovedSqlDocument document,
            RewriteOptions options = null,
            Diagnostics.IRewriteObserver observer = null)
        {
            return new SqlRewriteRegistry(document, options ?? new RewriteOptions(), observer);
        }

        /// <summary>Capabilities matching a stock modern SQL Server: CI_AS, 2022, level 160.</summary>
        public static DatabaseCapabilities Capabilities(
            string collation = "SQL_Latin1_General_CP1_CI_AS",
            int majorVersion = 16,
            int compatibilityLevel = 160,
            IEnumerable<string> indexes = null,
            IEnumerable<KeyValuePair<string, string>> procedures = null)
        {
            return new DatabaseCapabilities(
                collation,
                majorVersion,
                compatibilityLevel,
                indexes ?? new string[0],
                procedures);
        }

        /// <summary>
        /// A parameter collection as a command would carry it. Pass <c>null</c> for a
        /// supplied-but-null parameter and <see cref="System.DBNull.Value"/> for the
        /// database null a real provider hands over.
        /// </summary>
        public static System.Data.Common.DbParameterCollection Parameters(
            params (string Name, object Value)[] entries)
        {
            var collection = new FakeDbParameterCollection();

            foreach (var entry in entries)
            {
                collection.Add(new FakeDbParameter { ParameterName = entry.Name, Value = entry.Value });
            }

            return collection;
        }

        public static KeyValuePair<string, string>[] Procedures(params (string Name, string Hash)[] entries)
        {
            var result = new KeyValuePair<string, string>[entries.Length];

            for (var i = 0; i < entries.Length; i++)
            {
                result[i] = new KeyValuePair<string, string>(entries[i].Name, entries[i].Hash);
            }

            return result;
        }
    }
}
