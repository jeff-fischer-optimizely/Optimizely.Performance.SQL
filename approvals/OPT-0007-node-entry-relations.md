# OPT-0007 — ecf_NodeEntryRelations

| | |
| --- | --- |
| **Entry** | OPT-0007 |
| **Object created** | `dbo.ecf_NodeEntryRelations_optiperf_v1` |
| **Replaces the call to** | `dbo.ecf_NodeEntryRelations` |
| **Derived from** | EPiServer.Commerce.Core 15.1.0 / 14.46.0 — identical body in both |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Script** | [`ecf_NodeEntryRelations_optiperf_v1.sql`](../sql/ecf_NodeEntryRelations_optiperf_v1.sql) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed. The smallest change in the catalogue: one statement, one added hint.

## The change

`OPTION (RECOMPILE)` on the single statement joining the table-valued parameter. That is
the entire diff.

## Why

**Nothing is wrong with this query.** That is worth saying first, because the survey ranks
it high — over half a billion executions in the window, across 108 databases — and the
instinct on seeing a number like that is to look for a defect to fix.

There isn't one. The query is well written, the indexes support it, and the join is the
right join. What is wrong is the *estimate*: below compatibility level 150 the table-valued
parameter is a table variable and reports one row however many node-entry relations the
caller asked for. The plan is built for a batch of one and then run against a batch of
hundreds, half a billion times.

Telling the optimiser the truth is the whole fix. Nothing about the statement needed to
change.

## Equivalence

A recompile directive cannot change the rows returned. There is one statement, and its text
is otherwise character for character the original's.

This is as close to a no-op-with-a-plan-change as the catalogue gets, and it is the entry
against which the Low tier's meaning is easiest to state: *the rewrite cannot produce a
different answer, only a different plan.*

## Commerce 14 and 15

Body identical in both, so one entry, one hash, one object. Nothing to discriminate — unlike
[OPT-0005](OPT-0005-catalog-entry-components.md), where the bodies diverge, or
[OPT-0008](OPT-0008-catalog-entry-list.md), where they are identical but their callees are
not. Here identical bodies genuinely mean one decision.

## Gates

| Gate | Reason |
| --- | --- |
| `maximumCompatibilityLevel: 140` | At 150 and above the engine defers table-variable compilation itself. The hint would then be pure compile cost — and at half a billion executions, pure compile cost is not a rounding error. |
| `originalBodyHash` | Pins the reviewed body. |

The ceiling matters more here than anywhere else in the catalogue, for exactly that reason:
this is the highest-frequency statement of the Commerce entries, so it is the one where
paying an unnecessary compile per call would do the most damage. The gate is not boilerplate.

## What could make this wrong

- **Compile cost at this frequency, if the ceiling were ever removed or raised.** Half a
  billion executions is enough that the trade only works while the estimate is genuinely
  wrong.

Nothing else. There is no second statement, no callee, no collation dependency and no
result-set shape to preserve.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Script is valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hash pins the reviewed original | Recomputed from the shipped packages | `d472b986…22e12099a`, matches configuration |
| Commerce 14 and 15 bodies agree | Byte comparison of the two shipped scripts | Identical |
| Ceiling gate | Resolution simulated at levels 130 and 160 | Applies at 130, skipped at 160 |
| Behaviour under a real catalog workload | — | **Not done.** Field verification pending. |

## Before enabling

Nothing conditional. Deploy and enable.

If you want a single entry to sanity-check the compile-cost trade on before arming the rest
of the Commerce set, this is the one: highest frequency, smallest change, no confounding
factors.

## Rollback

Set `"enabled": false` on OPT-0007. The next call goes to Optimizely's procedure.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
