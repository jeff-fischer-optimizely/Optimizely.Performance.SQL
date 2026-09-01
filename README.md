# Optimizely.Performance.SQL

A runtime SQL statement rewrite shim for Optimizely CMS 11 and 12. It recognises a
statement the CMS is about to execute and substitutes a performance-approved variant,
without changing a line of application code — current or historical.

> **Status: early. Not production-ready yet.** The core decision engine is written and
> covered by tests, including an integration suite against a real SQL Server. The hosting
> adapters and the approved-SQL corpus are not written. See
> [What is not here yet](#what-is-not-here-yet) before you plan around this.

## Why

Optimizely CMS emits a fixed set of SQL statements. Some of them are slow for structural
reasons — table variables estimated at one row, `LOWER(col) = LOWER(@p)` predicates that
cannot seek, `CTE + ROW_NUMBER()` pagination where `OFFSET/FETCH` would do. The statements
are the same on every installation, so the fix is the same on every installation.

The problem is delivery. The statements are emitted from compiled Optimizely assemblies
across a decade of CMS versions. You cannot patch them at the source, you cannot ask
hundreds of customers to upgrade in step, and you cannot rewrite queries that customers'
own code depends on behaving exactly as it does.

So intercept them instead. Fingerprint each statement as it goes past, look it up in a
list of reviewed rewrites, and swap the text if — and only if — this specific database can
be shown to satisfy the conditions the rewrite was approved under.

## Design constraints

These are not preferences; they are the shape of the problem.

**Approval happens outside the code.** The review process is a markdown record under
`approvals/`, projected into `config/approved-sql.json` by a sync tool. Nothing that
ships in the assembly is unapproved, so there is no approval state to evaluate at runtime
and no code path that could execute an unapproved statement. `ApprovedStatement` carries
`ApprovedBy` / `ApprovedOn` / `ApprovalDocument` purely as provenance — support can trace a
live rewrite back to the record that authorised it, and nothing reads those fields.

**Never alter what Optimizely ships.** Stored procedures are not `ALTER`ed. The optimised
body is deployed under a new name and the shim changes which name gets called
(`RewriteKind.ProcedureRedirect`). If the replacement was never deployed, or a CMS upgrade
moved the original underneath us, the redirect withdraws itself and the shipped procedure
runs.

**No dynamic SQL.** Where a statement family needs to shed `(@p IS NULL OR col = @p)`
branches, every useful combination is authored and reviewed ahead of time and selected at
runtime by inspecting the parameters actually supplied. See
[Variants](#variants-instead-of-dynamic-sql).

**Prove the precondition, or do nothing.** A rewrite that is only correct under a
case-insensitive collation states that. A rewrite that only helps at compatibility level
150 states that. The shim probes the live database and skips anything it cannot show to be
safe. Skipping is always correct — the original statement still runs.

**Plug and play.** No index catalogue, no per-customer fleet matrix, no deployment
choreography. Drop the package in, point the factory at it, ship a config file.

## How it works

```
   CMS calls DbProviderFactory.CreateCommand()
                  │
   RewritingDbProviderFactory  ──►  decorates the real SqlClientFactory
                  │
   RewritingDbCommand.ExecuteReader()
                  │
                  ├─ CommandType is Text or StoredProcedure?     no ──► pass through
                  ├─ shim enabled and registry non-empty?        no ──► pass through
                  │
                  ├─ SqlNormalizer.Normalize(CommandText)
                  ├─ SqlFingerprint.ComputeFromNormalized(...)   ──► 128-bit hex key
                  │
                  ├─ SqlRewriteRegistry.Resolve(fingerprint, parameters, capabilities)
                  │        │
                  │        ├─ entry active for this CMS version?
                  │        ├─ DatabaseCapabilities.Satisfies(preconditions)?
                  │        ├─ first StatementVariant whose conditions hold
                  │        └─ shadow mode?
                  │
                  ├─ RewriteResult.ShouldReplace  ──►  swap CommandText, drop parameters
                  └─ IRewriteObserver.OnRewriteEvaluated(...)
                  │
   inner command executes
```

The overwhelmingly common answer is `RewriteResult.NoChange`, and the fast paths are built
around that: `RewriteContext.IsInert` short-circuits before any hashing, stored-procedure
commands skip out immediately unless the registry actually contains a redirect, and
normalisation happens once and feeds the fingerprint directly.

### Fingerprinting

`SqlFingerprint` is the leading 128 bits of the SHA-256 of the normalised statement, as
lower-case hex. Truncation is deliberate and safe: the key space is a closed set of a few
dozen reviewed statements, so this is a dictionary key, not a security boundary.

`ModuleHash` is different — full 256 bits over a stored procedure body, used to detect that
Optimizely has patched a procedure since its replacement was approved. It is computed
client-side rather than with `HASHBYTES`, because `HASHBYTES` rejects `nvarchar(max)` before
SQL Server 2016, and a probe that throws on an old engine would disable *every* rewrite on
that database rather than just the redirects.

### Variants instead of dynamic SQL

```jsonc
{
  "id": "OPT-0007",
  "fingerprint": "…",
  "originalSql": "SELECT … WHERE (@LanguageBranch IS NULL OR l.Name = @LanguageBranch) …",
  "variants": [
    {
      "id": "no-language",
      "when": [{ "parameter": "LanguageBranch", "test": "IsNull" }],
      "sql": "SELECT … /* language predicate removed entirely */",
      "dropsParameters": ["LanguageBranch"]
    },
    {
      "id": "language",
      "when": [{ "parameter": "LanguageBranch", "test": "IsNotNull" }],
      "sql": "SELECT … WHERE l.Name = @LanguageBranch …"
    }
  ]
}
```

Variants are evaluated in declaration order; the first whose conditions *all* hold wins.
`dropsParameters` strips parameters the chosen variant no longer references. This is a plan
concern rather than a correctness one — `sp_executesql` accepts a declared parameter the
batch never mentions — but leaving it declared keeps it in the signature, so the statement
caches under a different key and stays exposed to sniffing on a value it no longer uses.

### Preconditions

`RewritePreconditions` is checked against `DatabaseCapabilities` probed once per connection
string:

| Precondition | Guards against |
|---|---|
| `requiresCaseInsensitiveCollation` | Dropping `LOWER()` changes results under a `_CS_` collation |
| `requiresAccentInsensitiveCollation` | Same, for accent folding |
| `minimumCompatibilityLevel` | Optimiser-behaviour-dependent rewrites |
| `minimumSqlServerMajorVersion` | Syntax availability, e.g. `OFFSET/FETCH` needs 11 |
| `requiredIndexes` | A rewrite that regresses without its supporting index |

**Gate on compatibility level, not product version.** A fleet survey of all 690 Optimizely
DXP production databases found every one reporting engine build `12.0.2000.8` while
compatibility levels span 100 to 170 — a current Azure SQL engine generating 2008-era plans.
Product version cannot distinguish them, so `minimumSqlServerMajorVersion` should be left
null on essentially everything and `minimumCompatibilityLevel` should do the work. The same
survey found 4.6% of production off the majority collation, including one accent-insensitive
and one UTF-8 database — which is why the collation gates are not optional.

If probing is off (`ProbeDatabaseCapabilities = false`) or a probe fails,
`DatabaseCapabilities.Unknown` is returned and only unconditional rewrites apply. Failure
degrades toward doing nothing.

### Shadow mode

`RewriteOptions.ShadowMode` resolves every rewrite fully, reports it to observers as
`RewriteOutcome.Shadowed`, and then executes the original text. It is the intended first
step in production: you get the complete list of what *would* have changed, on real
traffic, with zero behaviour change.

Every non-rewrite outcome is reported too — `PreconditionsNotMet`,
`ReplacementProcedureMissing`, `OriginalProcedureDrifted`, `NoVariantMatched`. "Matched but
skipped" is the diagnostic case that matters.

## Configuration

`RewriteOptions`:

| Option | Default | Purpose |
|---|---|---|
| `Enabled` | `true` | Master kill switch. Decorators stay in the path and pass through. |
| `Version` | `All` | Which CMS version's rewrites to load. Set by the adapter. |
| `ShadowMode` | `false` | Resolve and report, but do not substitute. |
| `ConfigurationPath` | `config/approved-sql.json` | Relative paths resolve against the app base directory. |
| `ReloadOnChange` | `false` | Hot reload. Useful in staging; production config arrives with a deploy. |
| `AnnotateRewrittenSql` | `true` | Tag substituted statements with their rewrite id, visible in Query Store. |
| `ProbeDatabaseCapabilities` | `true` | Off means conditional rewrites never apply. |

A missing `approved-sql.json` yields an empty document and the site starts normally. A
*malformed* one throws at startup, because silently running unrewritten after a bad deploy
is worse than failing loudly.

## Layout

```
src/Optimizely.Performance.SQL.Core/     netstandard2.0 — shared by both CMS versions
  Ado/                RewritingDbProviderFactory / Connection / Command / Transaction
  Configuration/      ApprovedSqlDocument, ApprovedStatement, StatementVariant,
                      VariantCondition, RewritePreconditions, ProcedureRedirect,
                      RewriteKind, RewriteOptions, CmsVersion, ApprovedSqlLoader
  Fingerprinting/     SqlNormalizer, SqlFingerprint, ModuleHash
  Rewriting/          SqlRewriteRegistry, RewriteContext, RewriteResult,
                      DatabaseCapabilities, SqlServerCapabilityProvider
  Diagnostics/        IRewriteObserver, RewriteEvent, CompositeRewriteObserver

src/Optimizely.Performance.SQL.V12/      net6.0;net8.0 — CMS 12 adapter (csproj only so far)
```

The core targets `netstandard2.0` so one assembly serves .NET Framework 4.7.2 (CMS 11) and
.NET 6+ (CMS 12). It references neither `System.Data.SqlClient` nor
`Microsoft.Data.SqlClient` — everything is reached through `System.Data.Common`, so the shim
is not coupled to whichever SqlClient a given site resolves.

## Building

```bash
dotnet build Optimizely.Performance.SQL.slnx
dotnet test  Optimizely.Performance.SQL.slnx
```

## Testing

```
tests/Optimizely.Performance.SQL.Core.Tests/         210 tests, no database required
tests/Optimizely.Performance.SQL.Integration.Tests/   35 tests, needs a SQL Server
```

The unit suite drives the ADO.NET decorators over a fake provider that records what it was
actually asked to execute — the only place the substitution can be observed, since the
scope restores the command before control returns to the caller.

The integration suite builds two scratch databases on a real engine, one `_CI_AS` and one
`_CS_AS`, and runs the same collation-gated rewrite against both. That pairing is the point:
it shows the rewrite returning identical rows on the case-insensitive database, shows the
same rewrite genuinely changing results on the case-sensitive one, and shows the shim
standing down there. It also covers the procedure redirect end to end, including drift and
a replacement that was never deployed.

It targets a local default instance; override with `OPTIPERF_TEST_SQL`. With no server
reachable the integration tests skip rather than fail.

## What is not here yet

Being explicit, because the core reads more finished than the product is:

- **No approved statements.** No `config/approved-sql.json`, no `approvals/` records, no
  sync tool to project one into the other. The engine has nothing to run.
- **No CMS 12 adapter code.** `Optimizely.Performance.SQL.V12` is a csproj and a comment
  explaining the intended `DiagnosticListener` subscription. No source files.
- **No CMS 11 adapter at all.** The likely hook is subclassing
  `SqlServerDataStoreProvider`; not yet decided or written.
- **No CI.** The suites exist and pass; nothing runs them on push.
- **`SqlServerCapabilityProvider` matches indexes by name only.** It reads `sys.indexes`
  and compares names. A precondition that really wants "an index leading on these key
  columns with these includes" needs `sys.index_columns`, which is not wired up. Index
  preconditions are therefore weaker than they look.
- **CMS 13 is out of scope.** Its content store is not SQL Server, so a
  `DbProviderFactory`-based rewrite has nothing to attach to. It needs a separate strategy.

## License

MIT.
