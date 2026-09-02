# OPT-0002 — netContentListPaged (CMS 12)

| | |
| --- | --- |
| **Entry** | OPT-0002 |
| **Object created** | `dbo.netContentListPaged_optiperf_v1` |
| **Replaces the call to** | `dbo.netContentListPaged` |
| **Derived from** | EPiServer.Cms.Core 12.24.1 |
| **Risk** | Moderate |
| **Status** | Catalogued (`enabled: false`) |
| **Script** | [`sql/netContentListPaged_optiperf_v1.sql`](../sql/netContentListPaged_optiperf_v1.sql) |
| **Depends on** | [OPT-0006](OPT-0006-list-properties-over-threshold.md) |
| **Reviewed** | 2026-09-02 |

The CMS 11 procedure of the same name is a different body and a different decision. It is
[OPT-0010](OPT-0010-net-content-list-paged-cms11.md).

## Decision

Approved as written, disarmed, and disarmed for a different reason than
[OPT-0001](OPT-0001-ecf-order-search.md).

OPT-0001 is held back because its plan behaviour is unknown. This one is held back because
it has a known, narrow, non-equivalence — inherited from the function it calls — and no
site should enable it without first checking whether that case applies to their content.

The prize is large: 185 databases, and the survey attributes around a tenth of all fleet
logical reads to this procedure across twelve statements. It is second only to
`ecf_OrderSearch`.

## The change

Two things, of very different character.

**Seven `OPTION (RECOMPILE)` hints**, on the statements that join `@ContentItems`: the
language-resolution `UPDATE`, the language list, the version list, the page data, the
property load, the categories and the access rows. No join reordered, no predicate
rewritten, no column added or removed.

**One redirected call.** `GetListPropertiesOverThreshold` becomes
`GetListPropertiesOverThreshold_optiperf_v1`.

## Why the hints

`@ContentItems` is a table variable. Below compatibility level 150 it estimates at one row
regardless of what it holds. The estimate is not slightly wrong — it is wrong by the batch
size. A loader called with two hundred content ids gets a plan built for one, which means
nested loops and per-row seeks all the way down, against `tblContent`,
`tblContentLanguage`, `tblContentProperty` and `tblContentAccess` in turn, two hundred
times over.

That is not a defect in the procedure. It is the documented behaviour of table variables on
the optimiser half the surveyed estate is still running.

## Equivalence

The hints are result-preserving by inspection. `OPTION (RECOMPILE)` is a compilation
directive: it cannot change which rows a statement returns or the order it returns them in,
and there is no input for which the hinted and unhinted forms differ. It also does not
disturb the two `@@ROWCOUNT` tests, because a hint is part of the statement it is attached
to and does not become a statement of its own.

The redirected call is where the rating comes from.

## Why Moderate rather than Low

The hints alone would be Low. The entry is Moderate because of what it calls.

`GetListPropertiesOverThreshold_optiperf_v1` replaces a quadratic `WHILE` loop with one
set-based prefix reduction, and the two are not equivalent in one case: the prefix test is
`LIKE`, so a scope name containing an underscore can match a *different* scope of the same
length — `'a_c'` matches `'abc'`. The original breaks that tie by insertion order and keeps
one; the set-based form keeps both. Downstream, in this procedure, the extra row can
suppress a `LongString` that the original would have returned. The full argument is in
[OPT-0006](OPT-0006-list-properties-over-threshold.md).

**Risk does not stay behind a procedure call.** A caller is at least as risky as what it
calls, so this entry takes OPT-0006's tier rather than claiming its own. Rating it Low on
the grounds that "the hints are safe and the rest is somebody else's record" would be
exactly the kind of bookkeeping that lets a known defect ship.

## What could make this wrong

- **A list property scope name containing `_`, `%` or `[`.** This is the case above. It is
  the one thing a site must check before enabling, and it is checkable: look for those
  characters in `ScopeName` on `tblContentProperty` for rows with a non-null `ListIndex`.
- **Compilation cost outweighing the benefit.** Each execution now pays for seven compiles.
  For a procedure whose unhinted plans are mis-sized by two orders of magnitude that is a
  heavily favourable trade, but it is a trade — hence the gate below, not an unconditional
  entry.

## Gates, and why each one

| Gate | Reason |
| --- | --- |
| `appliesTo: V12` | The CMS 11 body is a different procedure with a different signature. |
| `maximumCompatibilityLevel: 140` | At 150 and above the engine defers table-variable compilation itself and reaches the same cardinality without the hint. Applying it there buys nothing and still charges seven compiles per call — a regression, not a fix. |
| `requiredProcedures: [GetListPropertiesOverThreshold_optiperf_v1]` | The replacement calls it. Without this the redirect would pass every gate and then fail at runtime with a missing object. |
| `originalBodyHash` | Pins the 12.24.1 body this copy was derived from. A CMS upgrade that changes the original stops the redirect. |

The ceiling is the useful one to understand: a database upgraded to level 150 stops
receiving this redirect at its next capability probe, with no configuration change and no
redeployment.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Script is valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hash pins the reviewed original | Recomputed from the shipped package | `a7be4b6e…725f3fd`, matches configuration |
| Callee dependency is enforced | Unit test: redirect suppressed when the function is absent | Suppressed, reported as `ReplacementProcedureMissing` |
| Ceiling gate selects correctly | Resolution simulated at levels 130 and 160 | Applies at 130, skipped at 160 |
| Behaviour on content with wildcard scope names | — | **Not done.** See below. |

## Before enabling

1. **Check the scope names.** On each candidate database, look for `_`, `%` or `[` in
   `tblContentProperty.ScopeName` where `ListIndex IS NOT NULL`. If any exist, do not
   enable this entry on that database. This is a hard stop, not a caution.
2. Deploy `GetListPropertiesOverThreshold_optiperf_v1` first. A wrong order is inert rather
   than broken — the `requiredProcedures` gate skips the redirect until the callee exists —
   but it wastes a change window.
3. Shadow-mode a page load carrying a large nested block list, which is the only content
   that reaches the delayed-scope path at all.
4. Confirm both `@@ROWCOUNT` early returns still fire: a batch of ids that exist and a batch
   that does not.

## Rollback

Set `"enabled": false` on OPT-0002. The next call goes to Optimizely's procedure.

`GetListPropertiesOverThreshold_optiperf_v1` becomes unreachable at the same moment — it is
only ever called from this replacement — so it can be left in place or dropped at leisure.

## Deferred to v2

Replacing `@ContentItems` with a `#temp` table. It would fix the estimate without a
per-call compile, and would carry real column statistics rather than just a row count, so
it is the better long-term answer.

It is not in v1 on purpose:

- it changes tempdb behaviour, which is a shared resource and not one this repository can
  reason about from the source;
- it interacts with the caller's transaction scope in ways a hint does not;
- and it cannot be argued correct by inspection the way a hint can, so it would have to be
  measured — which means it belongs behind a measurement, not in front of one.

To be revisited once OPT-0002 v1 has shadow-mode numbers to compare against. A v2 with no
v1 baseline would be a guess with more moving parts.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| Scope-name check (per database) | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
