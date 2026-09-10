# Compatibility level remediation

A step-by-step runbook for raising Optimizely CMS and Commerce databases to compatibility
level 150.

**This repository does not execute any of it.** The shim never runs DDL and never runs
`ALTER DATABASE`. What is here is the procedure, the classification query and the
verification query; running them stays with whoever owns the databases.

## Why this exists

The approved-SQL catalogue in [`sql/`](../sql/README.md) is a workaround. Nine of its
fourteen entries carry `maximumCompatibilityLevel: 140`, and every one of those nine exists
for the same reason: below level 150 the optimiser estimates a table variable at one row no
matter how many rows it holds, so a content loader called with two hundred IDs gets a plan
built for one. `OPTION (RECOMPILE)` forces compilation at execution time, when the count is
known. It works, and it is the wrong shape of fix — it buys back a correct estimate one
statement at a time, and charges a compile for it on every call.

Raising the database to 150 fixes the same problem for **every** statement in the database
at once, including the ones a shipped-procedure catalogue structurally cannot reach. Of the
165-statement costed corpus in [`docs/query-triage.md`](query-triage.md), 34.9% are customer
or add-on statements that no catalogue entry will ever touch, and a good share of those join
table variables too.

So this is not a complement to the catalogue. For the largest cost class it is a
**replacement**, and the catalogue is written to get out of the way when it lands.

## The estate

From the production fleet sweep, 690 of 691 live production databases probed:

| Tranche | Compatibility level | Databases | Share |
| --- | --- | ---: | ---: |
| **A** | 120, 130, 140 | 271 | 39.3% |
| **B** | 100, 110 | 115 | 16.7% |
| — | 150 and above | 304 | 44.1% |

**55.9% of production is below 150.** Over the affected-set survey — the databases carrying
the costed queries — the same gap accounts for 64.6% of attributed cost, which is the more
relevant number: the databases that are behind are disproportionately the ones that hurt.

Uniform across all 690: engine `12.0.2000.8`, EngineEdition 5 (Azure SQL Database),
`LEGACY_CARDINALITY_ESTIMATION = 0`, `PARAMETER_SNIFFING = 1`. Query Store is `READ_WRITE`
on 687 of 690. 55 of 421 customers run mixed levels across their own databases.

## Why 150, and why two tranches

Three optimiser boundaries matter, and they are not at the same level:

| Level | What arrives |
| --- | --- |
| **120** | The new cardinality estimator |
| **150** | Deferred table-variable compilation |
| **150** | Scalar UDF inlining |
| **150** | Batch mode on rowstore |

The thing we are actually after is deferred table-variable compilation, and it arrives at
150. The thing most likely to move a plan the wrong way is the cardinality estimator swap,
and it arrives at 120.

That difference is the whole reason for two tranches:

- **Tranche A (120/130/140)** is already running the new CE. Raising to 150 crosses one
  behavioural boundary, and it is the one we want. This is the easy tranche and it is also
  the larger one.
- **Tranche B (100/110)** is still on the legacy CE. A straight raise to 150 crosses two
  boundaries at once, and if something regresses there is no way to tell which one did it.
  So Tranche B raises the level *and* pins `LEGACY_CARDINALITY_ESTIMATION = ON` in the same
  change, which decouples them: the database gets deferred table-variable compilation now
  and the CE swap later, as a separate change with its own evidence.

Deferred table-variable compilation is gated on the compatibility level, not on the CE
version setting, so pinning legacy CE does not cost Tranche B the feature it came for. That
is the load-bearing claim of the two-tranche design, so it was measured rather than assumed.
A 5,000-row table variable joined to a real table, estimate read off the actual plan:

| Setting | Estimated rows on the `@tv` scan | Join |
| --- | ---: | --- |
| compatibility 140 | 1 | Nested Loops |
| compatibility 150 | 5,000 | Hash Match |
| compatibility 150, `LEGACY_CARDINALITY_ESTIMATION = ON` | 5,000 | Hash Match |

The third row is the one Tranche B rests on. Measured on SQL Server 2022; the probe that
produces it is section 6 of the verification script, so it can be re-run anywhere.

**The runbook stops at 150 on purpose.** Level 160 brings parameter-sensitive plan
optimisation and further changes we have no evidence about for this workload. Going to 160
may well be right later. It is a separate decision with a separate baseline, and bundling it
here would make a regression un-attributable.

## Phase 0 — Classify

Run [`sql/ops/compat-level-eligibility.sql`](../sql/ops/compat-level-eligibility.sql) in the
context of each database. Read-only; changes nothing.

It returns three result sets:

1. **The assessment** — tranche, current level, scoped configurations, Query Store state,
   scalar UDF inlineability, and the exact `ApplyCommand` and `RollbackCommand` for this
   database. Capture the whole row: `PreChangeLevel` is what you will need to reconstruct
   the rollback weeks later, and nothing else records it.
2. **Blockers and warnings** — empty means clear to proceed. `BLOCKER` rows stop the change.
3. **Catalogue impact** — which approved-SQL entries change behaviour on this database at
   150, filtered to the products actually installed.

**Do not proceed past a `BLOCKER`.** The three that occur in practice:

- *Query Store is not READ_WRITE.* Without it there is no before/after evidence and no
  Tier 1 rollback. Three fleet databases are in this state. Fix Query Store, let history
  accumulate, come back.
- *Database is not writable.* You are on a readable secondary. Reconnect to the primary.
- *Compatibility level is not one this runbook classifies.* Assess by hand.

## Phase 1 — Baseline

You cannot claim an improvement you did not measure beforehand, and you cannot roll back
surgically to a plan you never recorded.

1. Confirm Query Store `actual_state_desc = READ_WRITE` and that history goes back at least
   as far as the watch window. The eligibility script warns at fewer than three days of
   history and at `stale_query_threshold_days < 8`; raise retention to at least 14 days
   before the change if either fires.
2. Let a **full weekly cycle** accumulate before the cutover where you can. Optimizely
   estates are weekday-shaped; a baseline taken over a weekend describes nothing.
3. Review forced plans. A forced plan survives the level change, so those queries will not
   improve and will read as "no change" in the comparison. Decide per plan whether it is
   still needed — several exist only to work around the same estimate problem the raise
   fixes.
4. **Record the cutover time in UTC.** Every comparison in Phase 5 keys off it.

## Phase 2 — Pilot

Do not start with 271 databases.

Pick **one database per tranche**, on a customer whose workload you understand and whose
traffic profile is representative rather than extreme. Run the full runbook end to end on
those two, including the seven-day watch in Phase 6, before touching anything else.

The pilot is where you find out whether this estate behaves like the documentation says it
does. Two things specifically worth confirming there:

- Deferred table-variable compilation engages on the Tranche B pilot with legacy CE pinned.
  Verified on SQL Server 2022 (see the table above) but not yet on an Azure SQL Database
  carrying a real Optimizely schema. Section 6 of the verification script is the probe.
- Scalar UDF inlining is a win rather than a regression. Both product families ship scalar
  functions — CMS has `ConvertScopeName` and `ConvertIndexedScopeName`, Commerce has
  `fn_AreSiblings` and the `mdpfn_sys_*` family — so this estate is exposed to the level-150
  inlining change whether or not anyone was thinking about it. Which of them SQL Server will
  actually inline is not worth guessing from the body: the eligibility script reports
  `sys.sql_modules.is_inlineable`, which is authoritative.

## Phase 3 — Apply

In a change window, off-peak. The level change **clears the plan cache for the database**,
so the first minutes afterwards are a compile storm on top of whatever the site is doing.

**Tranche A** — one statement:

```sql
ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;
```

**Tranche B** — pin the CE first, in the same window:

```sql
ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = ON;
ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = 150;
```

Order matters. Setting the level first would run the database on the new CE for however
long it takes to run the second statement, which is exactly the uncontrolled exposure the
pin exists to prevent.

`ALTER DATABASE CURRENT` is used rather than naming the database: it is the form Azure SQL
Database wants, it cannot be run against the wrong database by copy-paste, and it works
identically on boxed SQL Server.

### One thing to deploy *before* the raise, on CMS 11

A CMS 11 database crossing 150 moves from OPT-0011 to OPT-0010 — from
`netContentListPaged_cms11_optiperf_v2` to `netContentListPaged_cms11_optiperf_v1`. If only
`_v2` was ever deployed, the redirect finds no replacement object and is skipped, and the
site quietly falls back to Optimizely's procedure with the four-bytes-at-a-time `WHILE`
loop.

That is a silent loss of the largest single improvement in the CMS 11 catalogue, not a
failure. Nothing breaks. But deploy
[`sql/netContentListPaged_cms11_optiperf_v1.sql`](../sql/netContentListPaged_cms11_optiperf_v1.sql)
before raising a CMS 11 database, and the crossover is seamless.

## Phase 4 — Recycle the application

**This is the step that gets missed.** It is not optional and it is not cosmetic.

The shim probes collation, product version, compatibility level, index presence and
procedure bodies **once per database and caches the answer for the lifetime of the
process**. Nothing invalidates that cache. After the raise, an application that has not been
recycled still believes the database is at its old level, and keeps applying every
`maximumCompatibilityLevel: 140` rewrite to a database that is now at 150.

Those rewrites are not wrong — they cannot change a result — but they are now pure cost. The
recompile hints charge a compile on every call to buy an estimate the engine is already
deferring for free. You will have paid for the raise and be measuring the old behaviour plus
overhead.

So: recycle every application process connected to the changed database, and do it before
Phase 5 starts collecting.

## Phase 5 — Verify

Run [`sql/ops/compat-level-verify.sql`](../sql/ops/compat-level-verify.sql) with
`@CutoverUtc` set to the time recorded in Phase 1. Read-only.

Five automated result sets:

| # | What it tells you |
| --- | --- |
| 1 | State — level, scoped configurations, Query Store, forced plan count, the windows being compared |
| 2 | Regressions, ranked by **total reads added**, not by percentage |
| 3 | Wins, ranked the same way |
| 4 | The net — the number that decides whether the change stays |
| 5 | Plans per regressed query, with the pre-cutover `plan_id` for a Tier 1 rollback |

The ranking choice in 2 and 3 is deliberate. A 900% regression on a query that runs twice an
hour is a curiosity; a 15% regression on `netContentListPaged` is the whole afternoon. Sort
by what it costs the server, not by how alarming the percentage looks.

Section 6 of the script is the table-variable probe, and it is **manual and commented out on
purpose**. Reading an estimate means finding a plan, and neither the plan cache nor Query
Store reliably retains a cheap ad-hoc probe — the cache may never keep the inner
`sp_executesql` batch, and Query Store's default `AUTO` capture mode discards infrequent
low-cost queries. A check that silently returns nothing is worse than no check. It is also
not needed per database: deferred table-variable compilation is not partial and not
conditional, and result set 1 already confirms the level took. Run it by hand on each
tranche's pilot, with the actual execution plan on, and take the answer as read for the rest
of the wave.

A first pass at 24 hours catches an outright disaster. The decision is made at seven days,
because a full weekly cycle is the shortest window in which an Optimizely workload
represents itself.

## Phase 6 — Watch

Seven days. What you are watching for:

- **Regressions that grow.** A plan that is 10% worse on day one and 60% worse on day four
  is usually a parameter-sniffing interaction that had not found its worst-case parameter
  set yet.
- **Batch or scheduled work.** Nightly imports, catalogue publishes, index maintenance and
  scheduled jobs will not appear in the first 24 hours and are frequently the heaviest
  table-variable users in the estate.
- **Timeouts rather than slow queries.** A regression that pushes a query past the client
  command timeout shows up as an error in the application, not as a slow query in Query
  Store. Watch application error rates alongside the database metrics.

## Rollback

Three tiers. **Use the smallest one that fixes the problem.** Reverting the level because
one query regressed throws away the benefit on every other query in the database.

### Tier 1 — Force the pre-cutover plan (surgical)

The right answer when one or two queries regressed and everything else improved.

```sql
EXEC sp_query_store_force_plan @query_id = <from result set 2>, @plan_id = <pre-cutover plan_id from result set 5>;
```

Effective in seconds, scoped to exactly the query that regressed, and it leaves the raise in
place. Result set 5 exists to give you the two IDs without hunting for them.

Record what you forced. A forced plan is a permanent liability if nobody knows why it is
there.

### Tier 2 — Turn off one level-150 feature (targeted)

The right answer when a *class* of query regressed and the class maps to a feature.

Scalar UDF regression — plans got worse around `ConvertScopeName`, `fn_AreSiblings`,
`mdpfn_sys_*` or a customer's own function:

```sql
ALTER DATABASE SCOPED CONFIGURATION SET TSQL_SCALAR_UDF_INLINING = OFF;
```

Broad plan movement on a Tranche A database, where the CE was never pinned:

```sql
ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = ON;
```

Both keep the database at 150, which means deferred table-variable compilation stays. That
is the point of preferring Tier 2 over Tier 3: it gives up the feature that broke and keeps
the one you came for.

### Tier 3 — Revert the level (last resort)

```sql
ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = <PreChangeLevel from Phase 0>;
```

Then **recycle the application again**, for the same reason as Phase 4 in reverse: until the
process re-probes, the shim believes the database is at 150 and is not applying the
level-140 rewrites the database now needs again.

On a Tranche B database, also decide whether to unpin `LEGACY_CARDINALITY_ESTIMATION`.
Leaving it pinned after reverting to 100 or 110 changes nothing — those levels use legacy CE
anyway — but leaving a scoped configuration set that nobody remembers setting will confuse
the next person. Unpin it, or write down why not.

Reverting is not free either way: it clears the plan cache a second time and produces a
second compile storm.

## Tranche B, stage two — removing the CE pin

Tranche B is not finished when the level reaches 150. It is finished when the database is
running the current cardinality estimator like everything else in the fleet, and that is a
second change with its own window, its own baseline and its own seven-day watch:

```sql
ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = OFF;
```

Leave at least two weeks between the two changes so the first has a settled baseline for the
second to be measured against. Verify with the same script, a new `@CutoverUtc`.

Rolling this one back is a single statement — set it back to `ON` — and the database stays
at 150. That is the payoff for having separated the two boundaries in the first place.

If stage two regresses on a given customer and there is no appetite to chase it, leaving the
pin in place indefinitely is an acceptable end state. It is a documented supported
configuration, the database keeps deferred table-variable compilation, and 100% of the
reason for this runbook is already banked. Record the decision per database.

## Fleet sequencing

386 databases across 421 customers is not one change. Suggested order, each stage gated on
the previous stage's seven-day watch coming back clean:

| Stage | Scope | Rationale |
| --- | --- | --- |
| 1 | Two pilot databases, one per tranche | Find out whether this estate behaves as documented |
| 2 | Tranche A, one wave per customer, smallest customers first | Larger tranche, single behavioural boundary, already on the new CE |
| 3 | Tranche B, same shape | Two boundaries, decoupled, so treat as the harder half |
| 4 | Tranche B stage two — unpin the CE | Separate change, separate evidence |

Two constraints worth respecting:

- **Do a customer's databases together, not one at a time.** 55 of 421 customers already run
  mixed levels across their own estate. Widening that gap makes "why is staging faster than
  production" harder to answer than it already is.
- **Keep a written record per database**: pre-change level, cutover UTC, tranche, whether
  the CE was pinned, verification verdict, anything forced. The eligibility script's first
  result set is designed to be captured whole for exactly this.

## What the raise fixes, and what it does not

Being explicit, because "we raised the compatibility level" invites the assumption that
performance work is done.

**Fixed by the raise:**

- The table variable one-row estimate, everywhere in the database, including the 34.9% of
  the costed corpus that is customer or add-on code no catalogue entry can reach.
- The reason nine of the fourteen catalogue entries exist.

**Not fixed by the raise** — these are query shape and physical design, not estimates, and
no compatibility level changes them:

- OPT-0001's materialised `DISTINCT` over every order metaclass table.
- OPT-0006's row-at-a-time prefix loop.
- OPT-0010's `WHILE` loop unpacking a `VARBINARY` four bytes at a time — the highest-execution
  statement in the survey. Note that this one gets *worse* relative to everything else after
  the raise, because the surrounding statements speed up and it does not.
- OPT-0009's column-side `LOWER()`.
- Every missing or badly-shaped index.
- The unparameterised clause concatenation in `ecf_OrderSearch`.

**Made unnecessary by the raise** — OPT-0002, OPT-0003, OPT-0005, OPT-0007, OPT-0008,
OPT-0011, OPT-0013, OPT-0014, OPT-0015 all stop firing at 150, by their own gates, with no
configuration change and no redeployment. That is the design working as intended, not
something to work around.

## What this runbook does not cover

- **Execution.** Nothing here runs automatically and nothing in the shim triggers it.
- **Reporting which tranche a live database is in from inside the application.** The shim
  knows — it probes the level — but it does not surface it. Deliberately out of scope for
  this round.
- **Levels above 150.** A separate decision, later, with its own baseline.
- **The three databases without Query Store.** They need Query Store enabled and history
  accumulated before they are candidates at all.
