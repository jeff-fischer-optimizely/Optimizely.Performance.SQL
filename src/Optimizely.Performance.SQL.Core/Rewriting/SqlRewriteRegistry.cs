using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Optimizely.Performance.SQL.Configuration;
using Optimizely.Performance.SQL.Diagnostics;
using Optimizely.Performance.SQL.Fingerprinting;

namespace Optimizely.Performance.SQL.Rewriting
{
    /// <summary>
    /// Immutable lookup from statement fingerprint to approved replacement, plus the
    /// variant-selection logic. This type is the whole decision engine; the ADO.NET
    /// decorators around it only move text.
    /// </summary>
    /// <remarks>
    /// Instances are read-only after construction and safe to share across threads.
    /// Configuration reloads build a new registry and swap the reference rather than
    /// mutating one in place.
    /// </remarks>
    public sealed class SqlRewriteRegistry
    {
        private readonly Dictionary<string, ApprovedStatement> _byFingerprint;
        private readonly RewriteOptions _options;
        private readonly IRewriteObserver _observer;

        public SqlRewriteRegistry(
            ApprovedSqlDocument document,
            RewriteOptions options,
            IRewriteObserver observer = null)
        {
            _options = options ?? new RewriteOptions();
            _observer = observer;
            _byFingerprint = new Dictionary<string, ApprovedStatement>(StringComparer.OrdinalIgnoreCase);

            var statements = document?.Statements ?? Array.Empty<ApprovedStatement>();

            foreach (var statement in statements)
            {
                if (statement == null)
                {
                    continue;
                }

                // Trust the stored fingerprint only if it is well-formed; otherwise derive
                // it. The sync tool guarantees the two agree, so this is belt-and-braces
                // for hand-edited configuration.
                var key = SqlFingerprint.IsValid(statement.Fingerprint)
                    ? statement.Fingerprint
                    : SqlFingerprint.Compute(statement.OriginalSql);

                if (key.Length == 0)
                {
                    continue;
                }

                // Last one wins, matching the sync tool's own duplicate handling.
                _byFingerprint[key] = statement;
            }

            Count = _byFingerprint.Count;
            ActiveCount = CountActive(statements, _options.Version);
        }

        /// <summary>Number of entries loaded, regardless of status.</summary>
        public int Count { get; }

        /// <summary>Number of entries eligible to fire for the configured CMS version.</summary>
        public int ActiveCount { get; }

        /// <summary>True when there is nothing to match, letting callers skip fingerprinting entirely.</summary>
        public bool IsEmpty
        {
            get { return _byFingerprint.Count == 0; }
        }

        /// <summary>
        /// Decides what to do with <paramref name="commandText"/>.
        /// </summary>
        /// <param name="commandText">Statement the CMS is about to execute.</param>
        /// <param name="parameters">Command parameters, used for variant selection. May be null.</param>
        /// <param name="capabilities">Probed facts about the target database.</param>
        public RewriteResult Resolve(
            string commandText,
            DbParameterCollection parameters,
            DatabaseCapabilities capabilities)
        {
            if (!_options.Enabled || _byFingerprint.Count == 0 || string.IsNullOrEmpty(commandText))
            {
                return RewriteResult.NoChange;
            }

            var fingerprint = SqlFingerprint.Compute(commandText);

            ApprovedStatement statement;
            if (fingerprint.Length == 0 || !_byFingerprint.TryGetValue(fingerprint, out statement))
            {
                return RewriteResult.NoChange;
            }

            if (!statement.IsActiveFor(_options.Version))
            {
                return Report(RewriteResult.Suppressed(statement, RewriteOutcome.NotActive), fingerprint, commandText);
            }

            var effective = capabilities ?? DatabaseCapabilities.Unknown;

            if (!effective.Satisfies(statement.Preconditions))
            {
                return Report(
                    RewriteResult.Suppressed(statement, RewriteOutcome.PreconditionsNotMet),
                    fingerprint,
                    commandText);
            }

            var variant = SelectVariant(statement, parameters);
            var sql = variant != null ? variant.Sql : statement.RewrittenSql;

            if (string.IsNullOrEmpty(sql))
            {
                return Report(
                    RewriteResult.Suppressed(statement, RewriteOutcome.NoVariantMatched),
                    fingerprint,
                    commandText);
            }

            if (_options.ShadowMode)
            {
                return Report(
                    RewriteResult.Suppressed(statement, RewriteOutcome.Shadowed),
                    fingerprint,
                    commandText);
            }

            if (_options.AnnotateRewrittenSql)
            {
                sql = Annotate(sql, statement, variant);
            }

            return Report(RewriteResult.Rewritten(statement, variant, sql), fingerprint, commandText);
        }

        /// <summary>
        /// Returns the first variant whose conditions all hold, or null to fall back to
        /// the statement's default rewrite.
        /// </summary>
        private static StatementVariant SelectVariant(ApprovedStatement statement, DbParameterCollection parameters)
        {
            var variants = statement.Variants;

            if (variants == null || variants.Length == 0)
            {
                return null;
            }

            foreach (var variant in variants)
            {
                if (variant == null || string.IsNullOrEmpty(variant.Sql))
                {
                    continue;
                }

                if (Matches(variant.When, parameters))
                {
                    return variant;
                }
            }

            return null;
        }

        private static bool Matches(VariantCondition[] conditions, DbParameterCollection parameters)
        {
            if (conditions == null || conditions.Length == 0)
            {
                return true;
            }

            foreach (var condition in conditions)
            {
                if (condition == null || string.IsNullOrEmpty(condition.Parameter))
                {
                    continue;
                }

                if (!Evaluate(condition, parameters))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Evaluate(VariantCondition condition, DbParameterCollection parameters)
        {
            var parameter = Find(parameters, condition.Parameter);
            var isPresent = parameter != null;
            var isNull = !isPresent || parameter.Value == null || parameter.Value == DBNull.Value;

            switch (condition.Test)
            {
                case ParameterTest.IsNull:
                    return isNull;

                case ParameterTest.IsNotNull:
                    return isPresent && !isNull;

                case ParameterTest.IsPresent:
                    return isPresent;

                case ParameterTest.IsAbsent:
                    return !isPresent;

                case ParameterTest.EqualsValue:
                    if (isNull)
                    {
                        return condition.Value == null;
                    }

                    return string.Equals(
                        Convert.ToString(parameter.Value, CultureInfo.InvariantCulture),
                        condition.Value,
                        StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Locates a parameter by name, tolerating the presence or absence of the leading
        /// sigil on either side. <see cref="DbParameterCollection.IndexOf(string)"/> is
        /// not consistent about this across providers, so the comparison is done here.
        /// </summary>
        private static IDataParameter Find(DbParameterCollection parameters, string name)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return null;
            }

            var wanted = Trim(name);

            for (var i = 0; i < parameters.Count; i++)
            {
                var candidate = parameters[i];

                if (candidate != null
                    && string.Equals(Trim(candidate.ParameterName), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string Trim(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName))
            {
                return string.Empty;
            }

            var first = parameterName[0];
            return first == '@' || first == ':' || first == '?'
                ? parameterName.Substring(1)
                : parameterName;
        }

        /// <summary>
        /// Prefixes a marker comment so the substitution is visible in Query Store,
        /// extended events and cached plans.
        /// </summary>
        private static string Annotate(string sql, ApprovedStatement statement, StatementVariant variant)
        {
            var id = variant == null || string.IsNullOrEmpty(variant.Id)
                ? statement.Id
                : statement.Id + "/" + variant.Id;

            return "/* opti-perf-sql:" + id + " */" + Environment.NewLine + sql;
        }

        private RewriteResult Report(RewriteResult result, string fingerprint, string originalSql)
        {
            if (_observer != null)
            {
                try
                {
                    _observer.OnRewriteEvaluated(new RewriteEvent(
                        result.DisplayId,
                        result.Reason,
                        fingerprint,
                        originalSql,
                        result.Sql));
                }
                catch
                {
                    // Never let diagnostics break the query.
                }
            }

            return result;
        }

        private static int CountActive(ApprovedStatement[] statements, CmsVersion version)
        {
            var count = 0;

            foreach (var statement in statements)
            {
                if (statement != null && statement.IsActiveFor(version))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
