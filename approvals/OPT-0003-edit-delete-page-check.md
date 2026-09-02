# OPT-0003 / OPT-0013 — editDeletePageCheckInternal

| | |
| --- | --- |
| **Entries** | OPT-0003 (CMS 12), OPT-0013 (CMS 11) |
| **Objects created** | `dbo.editDeletePageCheckInternal_optiperf_v1`, `dbo.editDeletePageCheckInternal_cms11_optiperf_v1` |
| **Replace the call to** | `dbo.editDeletePageCheckInternal` |
| **Derived from** | EPiServer.Cms.Core 12.24.1 and 11.21.5 |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Scripts** | [`editDeletePageCheckInternal_optiperf_v1.sql`](../sql/editDeletePageCheckInternal_optiperf_v1.sql), [`editDeletePageCheckInternal_cms11_optiperf_v1.sql`](../sql/editDeletePageCheckInternal_cms11_optiperf_v1.sql) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed.

This is the "what still points at the content I am about to delete" check, present in 95
surveyed databases and accounting for six of the statements in the survey. It is the entry
where the fix is most obviously matched to the diagnosis: an accumulator that is probed on
one column and has no index on it.

## The change

Two things, both mechanical.

**An index on the accumulator.** `@Result` gains `INDEX IX_Result_OwnerID NONCLUSTERED
(OwnerID)`. Both `DELETE`s filter on `OwnerID` and nothing else — one against `@pages`, one
against `tblContent` — and the final `SELECT` orders by it. The accumulator is now probed
where it used to be scanned.

**Eight `OPTION (RECOMPILE)` hints**, on the five `INSERT`s, the two `DELETE`s and the final
`SELECT`.

No predicate, join or projection was altered.

## Why

`@Result` is built by five `INSERT..SELECT`s, each filtering on `IN (SELECT ... FROM
@pages)` — a second table variable, arriving as a table-valued parameter. It is then pruned
by two `DELETE`s and returned sorted.

Below compatibility level 150 both table variables estimate at one row. So the five inserts
build nested-loop plans over `tblContentProperty`, `tblContentSoftlink` and two self-joins
of `tblContentLanguage` sized for a single page, while a bulk delete passes hundreds. The
two `DELETE`s then scan an unindexed heap once per probe.

Bulk delete is precisely the operation that makes `@pages` large, so the mis-estimate is
worst exactly when the procedure matters most.

## Equivalence

Neither change can affect the result.

`OPTION (RECOMPILE)` is a compilation directive. An index on a procedure-local table
variable is invisible outside the procedure: it changes how rows are found, never which
rows exist. `@Result` is declared, populated, read once and discarded inside this body, so
the index has no lifetime beyond the call and no interaction with anything a caller can
observe.

Two details that would matter if they had been overlooked, and were checked:

- **The index is not unique** and imposes no constraint. This procedure deliberately
  produces duplicate rows — one per referencing language — and a unique index would have
  turned that into an error.
- **`ORDER BY ReferenceType` on the final `SELECT` is untouched**, so output ordering is the
  original's. The index on `OwnerID` does not silently become the sort order.

## CMS 11 and CMS 12

Two scripts, one decision, and one place where they intentionally differ.

The CMS 11 body differs from CMS 12 by eleven lines, all of them `IS NOT NULL` guards CMS 12
added in two archive-reference `INSERT`s. **Those guards are not ported.** The CMS 11 copy
reproduces CMS 11's body exactly as it ships.

That is a deliberate call, and the reasoning is worth keeping: adding a CMS 12 correctness
change to a CMS 11 database would be a behaviour change wearing a performance rewrite's
clothes. If those guards fix a real defect on CMS 11 — they may well — that is a CMS
support matter with its own regression risk and its own owner. It is not something to smuggle
in under a recompile hint, where nobody would think to look for it.

## Gates, and why each one

| Gate | Reason |
| --- | --- |
| `maximumCompatibilityLevel: 140` | At 150 and above the engine defers table-variable compilation itself; the hints become pure compile cost on eight statements per call. |
| `minimumSqlServerMajorVersion: 12` | Inline index definitions on a table variable are SQL Server 2014 syntax. Every surveyed database is well past that, but CMS 11 is supported on SQL Server 2012, and the gate turns a deployment error into a skipped rewrite. |
| `appliesTo` | `V12` / `V11` respectively — different bodies. |
| `originalBodyHash` | Pins the reviewed body per version. |

The version floor is the interesting one. It guards a *syntax* availability, not a
behaviour, which is a different kind of gate from the rest of the catalogue: if it is wrong
the script fails to create at deploy time rather than misbehaving at run time. It is
there so that a CMS 11 site on SQL Server 2012 gets a skipped redirect instead of a failed
change window.

## What could make this wrong

- **Someone making the index unique**, or reusing this pattern on an accumulator that has a
  key. The duplicate rows here are the product, not an accident.
- **A future revision porting the CMS 12 null guards into the CMS 11 copy** because they
  look like an obvious improvement. See above.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Both scripts are valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hashes pin the reviewed originals | Recomputed from both shipped packages | CMS 12 `338bc5e1…f7f52da86`, CMS 11 `8d92cf4a…eb006fb3`; both match configuration |
| The eleven-line difference is only the null guards | Diff of the two shipped bodies | Confirmed; nothing else differs |
| Ceiling and version-floor gates | Resolution simulated across compatibility levels and server versions | Selects and skips as configured |
| Behaviour under a real bulk delete | — | **Not done.** Field verification pending. |

## Before enabling

Exercise a **bulk** delete, not a single-page one. A one-page delete is the case the
original was accidentally optimised for and will show nothing.

Worth confirming during field verification:

- the duplicate rows per referencing language still appear;
- the `ReferenceType` ordering is unchanged, since the UI groups on it;
- a delete of content with no references returns an empty set rather than erroring.

## Rollback

Set `"enabled": false` on OPT-0003 (or OPT-0013). The next call goes to Optimizely's
procedure.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
