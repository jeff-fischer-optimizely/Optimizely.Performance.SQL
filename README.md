# Optimizely.Performance.SQL

A runtime SQL statement rewrite shim for Optimizely CMS 11 and 12. It recognises a
statement the CMS is about to execute and substitutes a performance-approved variant,
without changing a line of application code — current or historical.

> **Status: early. Not production-ready yet.** The core decision engine and both hosting
> adapters are written and covered by tests, including suites that run against a real SQL
> Server and assert on what the *server* returned. The approved-SQL corpus is not written,
> so out of the box the engine has nothing to rewrite, and neither adapter has yet been run
> under a real Optimizely site. See [What is not here yet](#what-is-not-here-yet) before you
> plan around this.

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
[`approvals/`](approvals/README.md); `config/approved-sql.json` names the record that
authorised each entry in its `approvalDocument` field. The two are currently kept in step
by hand — there is no sync tool, and nothing verifies that the path resolves. Nothing that
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
choreography. Drop the package in, ship a config file.

**Edit the command; never replace it.** `SqlCommand` is sealed, and EPiServer casts a
`DbCommand` straight to `SqlCommand` at 25 sites in CMS 11 to reach provider-specific
members. Any decorator therefore dies on an `InvalidCastException` the moment it reaches
one of them. So the shim mutates the real command in place immediately before execution and
puts the original text back immediately after — the caller never sees a type it did not
create, and never sees text it did not set.

## How it works

Interception is version-specific; everything after it is shared.

```
  CMS 11 (net472)                        CMS 12 (net6.0+)
  System.Data.SqlClient                  Microsoft.Data.SqlClient
       │                                      │
  Harmony patches SqlCommand's            SqlClient raises
  8 public + 2 protected                  WriteCommandBefore and hands
  execution methods                       over the command itself
       │                                      │
       └──────────────► CommandRewriter.Apply(command, context) ◄──────────┘
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
   the command executes, then CommandRewrite.Restore() puts the original text back
```

The overwhelmingly common answer is `RewriteResult.NoChange`, and the fast paths are built
around that: `RewriteContext.IsInert` short-circuits before any hashing, stored-procedure
commands skip out immediately unless the registry actually contains a redirect, and
normalisation happens once and feeds the fingerprint directly.

### CMS 11: Harmony

.NET Framework's in-box `System.Data.SqlClient` publishes no diagnostic source, so there is
no supported hook and the only remaining option is IL patching. `SqlCommandPatcher` patches
every execution method `SqlCommand` declares — the eight public ones and, critically,
`ExecuteDbDataReader` / `ExecuteDbDataReaderAsync`, because `DbCommand.ExecuteReader()` is
non-virtual and dispatches to the protected pair. CMS 11 holds the command as a `DbCommand`
at roughly 97 sites, so a public-methods-only patch set would silently miss most of them.

Two details are load-bearing and were each settled by measurement rather than reasoning:

- **A finalizer, not a postfix.** Harmony skips postfixes when the original throws, which
  would leave a failed command holding rewritten text — fatal, because EPiServer retries
  deadlocks on the same command object. A finalizer runs on both paths. The measured cost is
  a stack trace truncated from 10 frames to 3; the exception type, message and the EPiServer
  call chain all survive.
- **A reentrancy guard.** `SqlCommand`'s execution methods call each other, so one logical
  call enters the patched set twice on every reader path. A `[ThreadStatic]` guard keyed by
  the command *instance* — not a bare flag — means a missed finalizer suppresses interception
  for that one command rather than poisoning the whole thread.

`[assembly: PreApplicationStartMethod]` installs before `Application_Start` and before
EPiServer initialisation, which matters because an Optimizely site does a great deal of its
slowest database work while starting up.

### CMS 12: DiagnosticSource

`Microsoft.Data.SqlClient` announces every command immediately before it executes and hands
over the command object, so this adapter patches nothing. It subscribes to
`SqlClientDiagnosticListener`, filtered to the three command events so SqlClient never
builds payloads for anything else, and matches `WriteCommandBefore` to `WriteCommandAfter`
or `WriteCommandError` by the payload's `OperationId`. A payload whose operation id cannot
be read has its rewrite withdrawn on the spot, since nothing could ever undo it later.

The payload's `Command` is read as `System.Data.Common.DbCommand` by reflection, so the
adapter carries no reference to `Microsoft.Data.SqlClient` and works with whichever version
the site resolves.

`AddOptimizelyPerformanceSql()` installs eagerly during `ConfigureServices` rather than on
first resolve, for the same startup-coverage reason. Calling `PerformanceSqlShim.Install()`
on the first line of `Program.cs` is earlier still.

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
| `Enabled` | `true` | Master kill switch, checked before anything is installed at all. |
| `Version` | `All` | Which CMS version's rewrites to load. Set by the adapter. |
| `ShadowMode` | `false` | Resolve and report, but do not substitute. |
| `ConfigurationPath` | `config/approved-sql.json` | Relative paths resolve against the app base directory. |
| `ReloadOnChange` | `false` | Hot reload. Useful in staging; production config arrives with a deploy. |
| `AnnotateRewrittenSql` | `true` | Tag substituted statements with their rewrite id, visible in Query Store. |
| `ProbeDatabaseCapabilities` | `true` | Off means conditional rewrites never apply. |

A missing `approved-sql.json` yields an empty document and the site starts normally. A
*malformed* one leaves the shim inert and reports why on `ShimStatus.LoadError` — the site
still starts, and still serves, because a bad config file is not a reason to take an
Optimizely site down. Check `ShimStatus` on startup if you want the loud version.

Failure discipline differs between the first load and later ones. A *reload* that fails
keeps the configuration already in force, because the likely cause is reading the file
halfway through an operator's save, and going inert there would silently switch the
optimisation off in production.

## Layout

```
src/Optimizely.Performance.SQL.Core/     netstandard2.0 — shared by both CMS versions
  Interception/       CommandRewriter, CommandRewrite, RewriteHost
  Configuration/      ApprovedSqlDocument, ApprovedStatement, StatementVariant,
                      VariantCondition, RewritePreconditions, ProcedureRedirect,
                      RewriteKind, RewriteOptions, CmsVersion, ApprovedSqlLoader
  Fingerprinting/     SqlNormalizer, SqlFingerprint, ModuleHash
  Rewriting/          SqlRewriteRegistry, RewriteContext, RewriteResult,
                      DatabaseCapabilities, SqlServerCapabilityProvider
  Diagnostics/        IRewriteObserver, RewriteEvent, CompositeRewriteObserver
  Ado/                RewritingDbProviderFactory / Connection / Command / Transaction

src/Optimizely.Performance.SQL.V11/      net472 — CMS 11 adapter
  Patching/           SqlCommandPatcher, SqlCommandPatch
  PerformanceSqlShim, PerformanceSqlStartup, AppSettingsOptions, ShimStatus

src/Optimizely.Performance.SQL.V12/      net6.0;net8.0 — CMS 12 adapter
  Diagnostics/        SqlClientDiagnosticSubscriber
  PerformanceSqlShim, PerformanceSqlServiceCollectionExtensions, ShimStatus
```

`Interception/` is the interception-agnostic middle: `CommandRewriter.Apply` takes any
`DbCommand`, returns a `CommandRewrite` that knows how to undo itself, and neither knows nor
cares whether a Harmony prefix or a diagnostic event called it. `RewriteHost` is the
composition root both adapters share — neither assembles the pieces itself, because the
pieces have to agree.

`Ado/` is the original decorator route. It is retained and tested, and it is the right
answer for a host that creates its own commands through a `DbProviderFactory`, but it is not
how either CMS adapter works: EPiServer's hard casts to `SqlCommand` rule a decorator out.

The core targets `netstandard2.0` so one assembly serves .NET Framework 4.7.2 (CMS 11) and
.NET 6+ (CMS 12). It references neither `System.Data.SqlClient` nor
`Microsoft.Data.SqlClient` — everything is reached through `System.Data.Common`, so the shim
is not coupled to whichever SqlClient a given site resolves. The V11 adapter's only
dependency beyond the framework is `Lib.Harmony`.

## Installing

**CMS 11** — reference the package. `[assembly: PreApplicationStartMethod]` installs it, so
there is nothing to call. Configure through `appSettings`:

```xml
<add key="optimizely:performance-sql:enabled" value="true" />
<add key="optimizely:performance-sql:shadowMode" value="true" />
<add key="optimizely:performance-sql:configurationPath" value="config/approved-sql.json" />
```

**CMS 12** — one line in `Program.cs`:

```csharp
builder.Services.AddOptimizelyPerformanceSql(options => options.ShadowMode = true);
```

Both install eagerly and both are idempotent; calling `Install()` again reloads
configuration rather than installing twice.

## Building

```bash
dotnet build Optimizely.Performance.SQL.slnx
dotnet test  Optimizely.Performance.SQL.slnx
```

## Testing

```
tests/Optimizely.Performance.SQL.Core.Tests/         210 tests, no database required
tests/Optimizely.Performance.SQL.Integration.Tests/   35 tests, needs a SQL Server
tests/Optimizely.Performance.SQL.V11.Tests/           26 tests, 15 need a SQL Server
tests/Optimizely.Performance.SQL.V12.Tests/           28 tests, 16 need a SQL Server
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

The two adapter suites install the real shim against a scratch database and ask the only
question that matters: did the approved SQL reach the server? The approved statements are
contrived so the two versions return *different* data — a `Source` column reading either
`original` or `replacement` — because real rewrites are semantically identical and therefore
useless for proving one actually ran. Every assertion reads a value the server produced.
This is the miniature form of watching a profiler; it is not a substitute for doing so
against a real site.

Both adapters get the same battery, deliberately: `DbCommand`-typed callers, a hard cast to
`SqlCommand`, async, `ExecuteScalar`, `ExecuteNonQuery`, transactions, procedure redirect,
restore after success, restore after a *failed* execution, command reuse, and
`Applied == Restored` with nothing left in flight. Two adapters reaching the command by
completely unrelated means can only be shown to behave alike by asking them the same
questions.

`PatchTargetTests` and `DiagnosticContractTests` need no database and cover what a real
provider cannot be made to do on demand: that the protected reader overrides are patched
(the gap that would silently lose ~97 CMS 11 call sites), and that the subscriber survives a
completion event that never arrives, a payload with no operation id, and disposal with work
still in flight.

The suites target a local default instance; override with `OPTIPERF_TEST_SQL`. With no
server reachable the database-backed tests skip rather than fail.

## What is not here yet

Being explicit, because the core reads more finished than the product is:

- **Nothing has been field-verified.** The catalogue exists — 14 entries in
  `config/approved-sql.json`, 15 scripts in [`sql/`](sql/README.md), review records in
  [`approvals/`](approvals/README.md) — and neither adapter has run under a real Optimizely
  site. The suites prove the mechanism against a scratch database. They do not prove it
  against Foundation with a profiler attached, which is the test that actually counts. Until
  then every entry is approved to *test*, not approved to *ship*.
- **Two of the three largest entries are disarmed.** OPT-0001 and OPT-0002 carry
  `enabled: false`, pending shadow-mode measurement and a per-database scope-name check
  respectively. That leaves about 11% of the costed-query corpus armed — plus the CMS 11
  share of `netContentListPaged`, which the corpus cannot separate out — against a catalogue
  that covers 34%. See [docs/query-triage.md](docs/query-triage.md).
- **The best available fix is a database setting this repository cannot apply.** 55.9% of
  the production fleet runs below compatibility level 150, and raising it fixes the largest
  cost class outright — including the third of the corpus no catalogue entry can reach. Nine
  of the fourteen entries exist only because that raise has not happened. The procedure, the
  eligibility query and the verification query are in
  [docs/compatibility-level-remediation.md](docs/compatibility-level-remediation.md) and
  [`sql/ops/`](sql/ops/); running them is a database-owner action, and none of it has been
  run yet.
- **No sync tool.** `approvals/` and `config/approved-sql.json` are kept in step by hand.
  A stale `approvalDocument` path would not be caught by anything.
- **CMS 11's patching is unproven outside a console host.** Three specific unknowns: whether
  Harmony patches an NGEN'd `System.Data.dll` under IIS as cleanly as it does a JIT-compiled
  one, whether DXP PaaS permits the dynamic-method emission Harmony needs, and how it
  coexists with an APM agent patching the same methods. All three are answerable only on a
  real host.
- **No CI.** The suites exist and pass; nothing runs them on push.
- **`SqlServerCapabilityProvider` matches indexes by name only.** It reads `sys.indexes`
  and compares names. A precondition that really wants "an index leading on these key
  columns with these includes" needs `sys.index_columns`, which is not wired up. Index
  preconditions are therefore weaker than they look.
- **CMS 13 is out of scope.** Its content store is not SQL Server, so there is nothing for a
  SQL rewrite to attach to. It needs a separate strategy.

## License

MIT.
