# OPT-0009 — netContentChildrenReferences

| | |
| --- | --- |
| **Entry** | OPT-0009 |
| **Object created** | `dbo.netContentChildrenReferences_optiperf_v1` |
| **Replaces the call to** | `dbo.netContentChildrenReferences` |
| **Derived from** | EPiServer.Cms.Core 12.24.1 |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Script** | [`netContentChildrenReferences_optiperf_v1.sql`](../sql/netContentChildrenReferences_optiperf_v1.sql) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed, with the payoff recorded honestly as negligible.

This entry is where the `LOWER()` investigation ended up, and its main value is as the
record of that investigation rather than as a performance fix. It is included because the
procedure was being copied anyway and there is no reason to carry a non-sargable predicate
forward — not because it is where this procedure's cost lives.

## The change

One predicate:

```sql
-- was
WHERE LOWER(LanguageID) = LOWER(@LanguageID)

-- is
WHERE LanguageID = @LanguageID
```

The eight child-listing branches are reproduced unchanged.

## Equivalence

Gated on `requiresCaseInsensitiveCollation`. Under a case-insensitive collation,
`LOWER(a) = LOWER(b)` and `a = b` agree for every pair of values, so the two predicates are
indistinguishable. If the capability probe cannot establish the collation, or establishes a
case-sensitive one, the redirect is skipped and Optimizely's procedure runs.

Two things that were checked because they are the ways this sort of change usually goes
wrong:

- **Accent sensitivity is deliberately *not* required.** `LOWER()` does not fold accents, so
  the original and the rewrite behave identically on an accent-sensitive collation. Requiring
  AI as well as CI would have excluded databases the rewrite is provably correct on — a gate
  that is stricter than the argument needs is not free, it just silently withholds the fix.
- **The `@@ROWCOUNT < 1` fallback is preserved.** When no language branch matches, the
  original takes a different branch based on `@@ROWCOUNT` immediately after the `SELECT`.
  `@@ROWCOUNT` is unaffected by making a predicate sargable, and the check remains the
  statement *directly following* the `SELECT` — which is the only way it stays correct. Any
  future edit that inserts a statement between them breaks this silently.

## Be clear about the size of this

`tblLanguageBranch` holds one row per configured language. A dozen rows, one page. Turning a
twelve-row scan into a seek saves essentially nothing.

The real cost of this procedure is the child listing: eight near-identical branches selecting
children of `@ParentID` ordered by `Created`, `Name`, `PeerOrder`, `StartPublish` or
`Changed`. `IDX_tblContent_fkParentID` already covers the seek and carries the right
`INCLUDE` list, so what remains is the sort, which is proportional to the number of children
under one parent and is inherent to the request. There is no rewrite for that. A content tree
with tens of thousands of children under one parent is an editorial problem, not a query
problem, and the honest advice for such a site is to restructure the tree.

139 databases have this procedure. None of them will notice this change.

## Where the LOWER() premise led

The starting hypothesis was that a whole low-risk tier existed here: check the collation, then
strip `LOWER()` from predicates across the schema, a no-risk change given the check.

Every `LOWER()` in the CMS and Commerce schemas was examined. The result:

- **Most are scalar assignments** — `SET @x = LOWER(@param)` — evaluated once and fully
  sargable thereafter. Nothing to fix.
- **Most of the rest are predicates against `LoweredUserName` and `LoweredRoleName`**,
  denormalised columns Optimizely maintains precisely so that the predicate stays sargable.
  Already optimal; "fixing" them would have been undoing someone's deliberate work.
- **Across all four shipped packages, exactly two column-side `LOWER()` predicates exist**:
  this one, and one in a Commerce metaclass DDL procedure that runs at deployment time
  against system tables and is not worth touching.

So the anticipated tier turned out to be a single query with a negligible payoff. That is
recorded here, and in the script header, rather than being quietly dropped or dressed up —
a hypothesis that did not pay off is a result, and the next person to have the same idea
should be able to find out it was already tested.

The tier that *did* turn out to be real is table-variable cardinality below compatibility
level 150, which affects just over half the surveyed fleet and accounts for most of the
Low-risk entries in this catalogue.

## Gates

| Gate | Reason |
| --- | --- |
| `requiresCaseInsensitiveCollation` | The entire equivalence argument rests on it. |
| `originalBodyHash` | Pins the reviewed body. |

No compatibility-level gate: there is no table variable here.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Script is valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hash pins the reviewed original | Recomputed from the shipped package | `bfa79ece…41cb4cc34`, matches configuration |
| Collation gate | Resolution simulated under CI and CS collations, and with an unprobed database | Applies under CI only |
| Every `LOWER()` in all four packages | Manual survey of the shipped scripts | Two column-side predicates in total; findings above |
| Child-listing behaviour | — | **Not done.** Field verification pending. |

## Before enabling

Nothing conditional beyond the gate, which the probe checks by itself.

Set expectations before measuring: if someone enables this hoping to see a change in a
trace, they will not, and the absence of one is not a fault. Confirm during field
verification that all eight ordering branches still return the ordering they should — the
branches are the part with something to get wrong, not the predicate.

## Rollback

Set `"enabled": false` on OPT-0009. The next call goes to Optimizely's procedure.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
