# OPT-0010 / OPT-0011 — netContentListPaged (CMS 11)

| | |
| --- | --- |
| **Entries** | OPT-0010 (level 150+), OPT-0011 (level 140 and below) |
| **Objects created** | `dbo.netContentListPaged_cms11_optiperf_v1`, `dbo.netContentListPaged_cms11_optiperf_v2` |
| **Replace the call to** | `dbo.netContentListPaged` |
| **Derived from** | EPiServer.Cms.Core 11.21.5 |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Scripts** | [`netContentListPaged_cms11_optiperf_v1.sql`](../sql/netContentListPaged_cms11_optiperf_v1.sql), [`netContentListPaged_cms11_optiperf_v2.sql`](../sql/netContentListPaged_cms11_optiperf_v2.sql) |
| **Reviewed** | 2026-09-02 |

The CMS 12 procedure of the same name is a different body and a different decision, rated
Moderate. It is [OPT-0002](OPT-0002-net-content-list-paged.md). These two share a name with
it and very little else — different signature, different table, different column names,
roughly 78 lines apart.

## Decision

Approved and armed, as a pair covering disjoint compatibility windows.

This is the largest single item in the fleet by execution count, and it is the one change in
the catalogue that was *tested* rather than only argued. Both facts are why it is treated at
more length than its Low rating suggests.

## The problem

CMS 11 has no table-valued parameter here. The caller packs content ids into a
`VARBINARY(8000)` and the procedure unpacks them four bytes at a time:

```sql
SET @Index = 1
SET @Length = DATALENGTH(@Binary)
WHILE (@Index <= @Length)
BEGIN
    INSERT INTO @ContentItems(LocalPageID) VALUES(SUBSTRING(@Binary, @Index, 4))
    SET @Index = @Index + 4
END
```

One `INSERT` statement per id, up to 2,000 statement round trips per call.

**That single `INSERT` is the highest-execution statement in the entire fleet survey: 7.6
billion executions across 99 databases.** Not the most expensive per execution — nowhere
near — but nothing else in the corpus is executed as often, and it is doing work that does
not need to be done at all.

## The change

**Both versions** replace the loop with a tally-CTE unpack:

```sql
;WITH E1(n) AS (SELECT 1 FROM (VALUES(1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) v(n)),
      E2(n) AS (SELECT 1 FROM E1 a CROSS JOIN E1 b),
      E4(n) AS (SELECT 1 FROM E2 a CROSS JOIN E2 b),
      Positions(Ordinal) AS
      (
          SELECT TOP ((CONVERT(INT, @Length) + 3) / 4)
                 ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
          FROM E4
      )
INSERT INTO @ContentItems(LocalPageID)
SELECT SUBSTRING(@Binary, (Ordinal - 1) * 4 + 1, 4)
FROM Positions
```

**v2 additionally** carries `OPTION (RECOMPILE)` on the seven statements that read
`@ContentItems`.

## Equivalence of the unpack

The CTE generates offsets 1, 5, 9, … for `ceil(@Length / 4)` rows. That is exactly the set of
offsets the loop visited, and `ceil(@Length / 4)` is exactly its termination test
`@Index <= @Length`, including the case where the payload is not a multiple of four bytes and
the loop makes one final short read.

Insert order is not preserved. It does not need to be: every statement that reads
`@ContentItems` has an explicit `ORDER BY`, and none of them orders by anything derived from
insertion sequence. This was checked statement by statement rather than assumed, because it
is the assumption the whole change rests on.

## What was actually tested

This is the largest behavioural change in the catalogue, so it was differential-tested rather
than only reasoned about:

- both forms — the original `WHILE` loop and the tally unpack — were run against the same
  inputs;
- 200 randomly generated payloads;
- 0 to 300 ids each, so both the empty payload and realistic batch sizes are covered;
- roughly one in seven truncated to a **non-multiple of four bytes**, to exercise the partial
  tail, which is the case an off-by-one would land in;
- compared as multisets, so a difference in either content or multiplicity would fail.

**Identical in every trial.**

That does not make the change proven — 200 trials is not a proof, and the argument above is
what carries the claim — but it does mean the specific mistakes this rewrite is prone to
(off-by-one on the tail, a dropped final id, a duplicated first one) were looked for with
inputs designed to find them, rather than declared unlikely.

## Equivalence of the hints (v2 only)

`OPTION (RECOMPILE)` is a compilation directive and cannot change which rows a statement
returns. It does not disturb the `@@ROWCOUNT` tests that follow two of the `SELECT`s, because
a hint is part of the statement it is attached to rather than a statement of its own.

Neither change depends on the optimiser choosing well — only on it being given true
information.

## Why two entries rather than one

The loop is worth fixing at **every** compatibility level. The hints are worth applying only
below 150.

A single entry capped at 140 would have left the biggest execution-count item in the fleet
unfixed on every database that had upgraded. A single uncapped entry would have charged seven
unnecessary compiles per call on those same databases. So: two bodies, two entries, disjoint
windows.

| Entry | Window | Carries |
| --- | --- | --- |
| OPT-0010 → `_v1` | `minimumCompatibilityLevel: 150` | unpack only |
| OPT-0011 → `_v2` | `maximumCompatibilityLevel: 140` | unpack + seven recompile hints |

Exactly one is ever eligible on a given database. A database upgrading from 140 to 150 crosses
between them at its next capability probe, with no configuration change and no redeployment —
which is the behaviour the ceiling-and-floor pair exists to produce.

The seven statements the v2 hints cover are the language-resolution `UPDATE`, the language
list, the version list, the page data, the property load, the categories and the access rows.
The property load is the expensive one: a nested loop into `tblContentProperty` per page is
fine for one page and not for two thousand.

## What this pair exposed in the shim

Nothing about it was safe to ship until the registry was fixed. Redirects were indexed by
original procedure name alone, so the second entry for `netContentListPaged` silently
overwrote the first — and with CMS 11 and CMS 12 both claiming that name too, three of the
four were being discarded at load time. `IsActiveFor` runs *after* the lookup, so `appliesTo`
could not disambiguate either.

The registry now keeps a candidate list per procedure name and tries each in turn. That is a
core change driven entirely by this entry, and it is the reason the disjoint-window pattern is
available to the rest of the catalogue at all.

## What could make this wrong

- **A future statement reading `@ContentItems` that depends on insertion order.** There is no
  such statement today and the pattern gives no reason to add one, but it is the assumption
  that would break silently rather than loudly.
- **The `_v1` / `_v2` files being deployed under the wrong names.** They create distinct
  objects on purpose. An earlier draft had the CMS 11 replacements creating
  `netContentListPaged_optiperf_v1` — the same object name as the CMS 12 replacement — which
  would have pointed a redirect at a body with an incompatible signature. Renamed to
  `netContentListPaged_cms11_optiperf_v1` / `_v2`.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Both scripts are valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Unpack equivalence | Differential test, 200 randomised payloads, partial tails included | Identical multisets in every trial |
| No reader depends on insert order | Statement-by-statement review of all seven consumers | Every one has an explicit `ORDER BY`; none derived from insertion sequence |
| Body hash pins the reviewed original | Recomputed from the shipped package | `3361392a…18e9b77c05`, matches both entries |
| Windows are disjoint and select correctly | Resolution simulated at levels 130 and 160 | 130 → `_v2`, 160 → `_v1`; never both, never neither |
| Object names do not collide with the CMS 12 copy | Name review across all fifteen scripts | Distinct |
| Behaviour under a real CMS 11 workload | — | **Not done.** Field verification pending. |

## Before enabling

1. Deploy **both** scripts. They cover different databases, and a database can move between
   them on a compatibility-level change without anyone redeploying.
2. During field verification, confirm in the Profiler trace which variant fired, and that it
   matches the target database's compatibility level. The pair is the catalogue's only
   floor-and-ceiling split, so it is the one worth confirming empirically rather than trusting.
3. Exercise a large batch — this is a *paged* loader and the whole point is behaviour at two
   thousand ids, not at ten.
4. Exercise an empty payload (`DATALENGTH(@Binary) = 0`). The loop and the CTE both produce
   zero rows, and it was in the differential test, but it is also the cheapest thing to check.

## Rollback

Set `"enabled": false` on OPT-0010 and OPT-0011. Note that they are separate entries:
disabling one leaves the other armed for its own compatibility window, which may be what you
want during a staged rollout and is a surprise if it is not.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| Reviewed the differential test | | |
| DBA (deployment — both scripts) | | |
| Field verification (Foundation + Profiler) | | |
