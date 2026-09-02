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

        /// <summary>
        /// Redirects grouped by the procedure they replace, in configuration order.
        /// </summary>
        /// <remarks>
        /// A list rather than a single entry because one procedure name routinely has more
        /// than one approved replacement. Two reasons, both structural rather than
        /// incidental. CMS 11 and CMS 12 ship different bodies under the same name, so each
        /// needs its own replacement and its own drift hash. And a rewrite whose benefit
        /// depends on the optimiser can need one body below a compatibility level and a
        /// different one above it -- the table variable entries pair a recompile-hinted copy
        /// capped at level 140 with an unhinted copy from 150 up.
        ///
        /// Candidates are tried in the order the configuration lists them and the first
        /// whose gates all pass wins, so overlapping entries resolve by precedence rather
        /// than by error. Entries intended to be mutually exclusive should still be written
        /// with disjoint preconditions; the ordering is a tie-break, not a substitute for
        /// saying which database each one is for.
        /// </remarks>
        private readonly Dictionary<string, List<ApprovedStatement>> _byProcedure;
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
            _byProcedure = new Dictionary<string, List<ApprovedStatement>>(StringComparer.OrdinalIgnoreCase);

            var statements = document?.Statements ?? Array.Empty<ApprovedStatement>();

            foreach (var statement in statements)
            {
                if (statement == null)
                {
                    continue;
                }

                if (statement.Kind == RewriteKind.ProcedureRedirect)
                {
                    var redirect = statement.Procedure;

                    if (redirect == null
                        || string.IsNullOrEmpty(redirect.OriginalName)
                        || string.IsNullOrEmpty(redirect.ReplacementName))
                    {
                        continue;
                    }

                    var name = DatabaseCapabilities.Normalize(redirect.OriginalName);

                    List<ApprovedStatement> candidates;
                    if (!_byProcedure.TryGetValue(name, out candidates))
                    {
                        candidates = new List<ApprovedStatement>(1);
                        _byProcedure[name] = candidates;
                    }

                    candidates.Add(statement);
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

            // Counts entries, not procedure names, so a procedure with a CMS 11 and a CMS 12
            // replacement reports as the two approvals it is.
            var redirectCount = 0;
            foreach (var candidates in _byProcedure.Values)
            {
                redirectCount += candidates.Count;
            }

            Count = _byFingerprint.Count + redirectCount;
            ActiveCount = CountActive(statements, _options.Version);
        }

        /// <summary>Number of entries loaded.</summary>
        public int Count { get; }

        /// <summary>Number of entries eligible to fire for the configured CMS version.</summary>
        public int ActiveCount { get; }

        /// <summary>True when there is nothing to match, letting callers skip fingerprinting entirely.</summary>
        public bool IsEmpty
        {
            get { return _byFingerprint.Count == 0 && _byProcedure.Count == 0; }
        }

        /// <summary>True when no procedure redirects are configured, letting callers skip the lookup.</summary>
        public bool HasProcedureRedirects
        {
            get { return _byProcedure.Count != 0; }
        }

        /// <summary>
        /// Every procedure name the redirects reference, originals and replacements alike.
        /// </summary>
        /// <remarks>
        /// Hand this to <see cref="SqlServerCapabilityProvider"/> at construction so the
        /// probe fetches exactly these bodies and no others. Empty when nothing is under
        /// redirect, which switches the procedure probe off entirely.
        /// </remarks>
        public IReadOnlyCollection<string> ProcedureNames
        {
            get
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var candidates in _byProcedure.Values)
                {
                    foreach (var statement in candidates)
                    {
                        names.Add(statement.Procedure.OriginalName);
                        names.Add(statement.Procedure.ReplacementName);

                        // Procedures a precondition names are probed too, or the precondition
                        // that requires them could never be satisfied.
                        var preconditions = statement.Preconditions;

                        if (preconditions == null)
                        {
                            continue;
                        }

                        if (preconditions.RequiredProcedures != null)
                        {
                            foreach (var name in preconditions.RequiredProcedures)
                            {
                                if (!string.IsNullOrEmpty(name))
                                {
                                    names.Add(name);
                                }
                            }
                        }

                        if (preconditions.RequiredProcedureBodies != null)
                        {
                            foreach (var expected in preconditions.RequiredProcedureBodies)
                            {
                                if (!string.IsNullOrEmpty(expected.Key))
                                {
                                    names.Add(expected.Key);
                                }
                            }
                        }
                    }
                }

                return names;
            }
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
        /// Decides whether a stored procedure call should be pointed at a versioned copy.
        /// </summary>
        /// <param name="procedureName">Procedure the command is about to execute.</param>
        /// <param name="capabilities">Probed facts about the target database.</param>
        /// <remarks>
        /// <para>
        /// Three things must hold before the call is redirected: the replacement must
        /// actually exist in this database, the original must still hash to what the
        /// replacement was written against, and the entry's own preconditions must be
        /// satisfied. Any of them failing leaves the shipped procedure to run.
        /// </para>
        /// <para>
        /// A procedure may have several approved replacements -- a CMS 11 body and a CMS 12
        /// one, or a pair covering different compatibility levels. Each candidate is put
        /// through those same three gates in configuration order and the first to clear them
        /// all is used. When none clears, the reason reported is that of the candidate that
        /// got furthest, which is the one that was nearly right; see <see cref="GateProgress"/>.
        /// </para>
        /// </remarks>
        public RewriteResult ResolveProcedure(string procedureName, DatabaseCapabilities capabilities)
        {
            if (!_options.Enabled || _byProcedure.Count == 0 || string.IsNullOrEmpty(procedureName))
            {
                return RewriteResult.NoChange;
            }

            List<ApprovedStatement> candidates;
            if (!_byProcedure.TryGetValue(DatabaseCapabilities.Normalize(procedureName), out candidates))
            {
                return RewriteResult.NoChange;
            }

            var effective = capabilities ?? DatabaseCapabilities.Unknown;

            ApprovedStatement statement = null;
            ProcedureRedirect redirect = null;
            RewriteOutcome bestRejection = RewriteOutcome.NotActive;
            ApprovedStatement bestRejected = null;
            var bestProgress = -1;

            foreach (var candidate in candidates)
            {
                var rejection = Rejects(candidate, effective);

                if (rejection == null)
                {
                    statement = candidate;
                    redirect = candidate.Procedure;
                    break;
                }

                var progress = GateProgress(rejection.Value);

                if (progress > bestProgress)
                {
                    bestProgress = progress;
                    bestRejected = candidate;
                    bestRejection = rejection.Value;
                }
            }

            if (statement == null)
            {
                return Report(RewriteResult.Suppressed(bestRejected, bestRejection), null, procedureName);
            }

            if (_options.ShadowMode)
            {
                return Report(RewriteResult.Suppressed(statement, RewriteOutcome.Shadowed), null, procedureName);
            }

            return Report(
                RewriteResult.Redirected(statement, redirect.ReplacementName),
                null,
                procedureName);
        }

        /// <summary>
        /// How far through the gates a rejected candidate got, so the most informative
        /// rejection can be the one reported.
        /// </summary>
        /// <remarks>
        /// Reporting the first candidate's rejection is misleading once entries are
        /// version-specific, because most of them were never meant to apply. A Commerce 14
        /// database above the compatibility ceiling would report OriginalProcedureDrifted
        /// from the Commerce 15 entry -- sending an operator to look for a CMS patch that
        /// does not exist -- when the honest answer is the ceiling. The candidate that got
        /// furthest is the one that was nearly right, so its reason is the useful one.
        /// </remarks>
        private static int GateProgress(RewriteOutcome rejection)
        {
            switch (rejection)
            {
                case RewriteOutcome.NotActive: return 0;
                case RewriteOutcome.ReplacementProcedureMissing: return 1;
                case RewriteOutcome.OriginalProcedureDrifted: return 2;
                case RewriteOutcome.PreconditionsNotMet: return 3;
                default: return 0;
            }
        }

        /// <summary>
        /// Why <paramref name="statement"/> cannot be used against this database, or null
        /// when it can.
        /// </summary>
        /// <remarks>
        /// Every gate here is fail-closed: anything unknown or unproven rejects, so the
        /// procedure Optimizely shipped is what runs. That is what makes it safe to try
        /// several candidates in a row -- the loop can only ever end in a redirect that
        /// passed all three checks, or in no redirect at all.
        /// </remarks>
        private RewriteOutcome? Rejects(ApprovedStatement statement, DatabaseCapabilities capabilities)
        {
            if (!statement.IsActiveFor(_options.Version))
            {
                return RewriteOutcome.NotActive;
            }

            var redirect = statement.Procedure;

            // The redirect is only meaningful against a probed database: without knowing
            // what is deployed we cannot tell a missing replacement from a present one.
            if (!capabilities.IsProbed || !capabilities.HasProcedure(redirect.ReplacementName))
            {
                return RewriteOutcome.ReplacementProcedureMissing;
            }

            // A hash is required. Without one we cannot show the original still matches
            // what the replacement was written against, so we do not redirect.
            if (!capabilities.ProcedureBodyMatches(redirect.OriginalName, redirect.OriginalBodyHash))
            {
                return RewriteOutcome.OriginalProcedureDrifted;
            }

            if (!capabilities.Satisfies(statement.Preconditions))
            {
                return RewriteOutcome.PreconditionsNotMet;
            }

            return null;
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
