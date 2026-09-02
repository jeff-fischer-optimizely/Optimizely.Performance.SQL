# OPT-0005 / OPT-0014 — ecf_CatalogEntry_Components

| | |
| --- | --- |
| **Entries** | OPT-0005 (Commerce 15), OPT-0014 (Commerce 14) |
| **Objects created** | `dbo.ecf_CatalogEntry_Components_optiperf_v1`, `dbo.ecf_CatalogEntry_Components_com14_optiperf_v1` |
| **Replace the call to** | `dbo.ecf_CatalogEntry_Components` |
| **Derived from** | EPiServer.Commerce.Core 15.1.0 and 14.46.0 |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Scripts** | [`ecf_CatalogEntry_Components_optiperf_v1.sql`](../sql/ecf_CatalogEntry_Components_optiperf_v1.sql), [`ecf_CatalogEntry_Components_com14_optiperf_v1.sql`](../sql/ecf_CatalogEntry_Components_com14_optiperf_v1.sql) |
| **Called by** | [OPT-0008 / OPT-0015](OPT-0008-catalog-entry-list.md) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed, as two entries rather than one. The split is the substance of this
record; the performance change itself is the least interesting thing in it.

## The change

`OPTION (RECOMPILE)` on the statements joining `@CatalogEntryIds`. Three of them in
Commerce 15, four in Commerce 14. Same tables, same joins, same projections, same
response-group branching, same result sets in the same order.

Below compatibility level 150 a table-valued parameter *is* a table variable and estimates
at one row, so every one of those statements gets a nested-loop plan sized for a single
catalog entry while the loader passes a page of them. The survey records these statements in
the hundreds of millions of executions across 100 databases.

## Equivalence

Recompile directives cannot change which rows a statement returns. There is no second
component to this argument — unlike OPT-0002, nothing here calls anything whose behaviour
changed.

## Why two entries

This is the part worth reading, because getting it wrong would have been a silent data
defect rather than a performance regression.

**Commerce 14 returns one more result set than Commerce 15.** In the variation branch,
Commerce 14 selects from `Variation` and then again from `Merchant`:

```sql
SELECT m.*
FROM Merchant m
INNER JOIN Variation v ON m.MerchantId = v.MerchantId
INNER JOIN @CatalogEntryIds N ON N.ContentId = v.CatalogEntryId
```

Commerce 15 dropped it. The first version of this work claimed a single replacement covered
both, on the strength of the two bodies looking alike. They do not: a Commerce 14 caller
pointed at the Commerce 15 copy would receive four result sets where its reader expects
five, and would fail — or worse, succeed while silently missing merchant data, depending on
how the reader advances.

So there are two bodies. The Commerce 14 copy reproduces the `Merchant` select and keeps all
four result sets in their original order.

The two shipped bodies hash differently, so the `originalBodyHash` gate alone is enough to
keep them apart: a database can only ever match the entry written for the version it runs.
No ordering, no precedence, no configuration discipline required.

That is not true of the *caller*, `ecf_CatalogEntry_List`, which is byte-identical across
the two Commerce versions. How that one is discriminated is
[OPT-0008](OPT-0008-catalog-entry-list.md)'s problem, and it is solved by keying on *this*
procedure's hash.

## Gates, and why each one

| Gate | Reason |
| --- | --- |
| `maximumCompatibilityLevel: 140` | Above that the engine defers table-variable compilation itself and the hints become pure compile cost. |
| `originalBodyHash` | Pins the reviewed body — and, as above, is what separates Commerce 14 from Commerce 15. |

`appliesTo` is `All`: this is Commerce, and it is reached from both CMS 11 and CMS 12 hosts.
The CMS version is irrelevant to it, and constraining it would have excluded valid
databases for no reason.

## What could make this wrong

- **Adding a result set, removing one, or reordering them.** A reader positioned by ordinal
  has no way to detect the mistake. This is the failure mode the split exists to prevent,
  and it is the thing to check first in any future revision of either body.
- **A Commerce release changing the original.** Caught by the hash; the redirect stops.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Both scripts are valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hashes pin the reviewed originals | Recomputed from both shipped packages | COM 15 `5eb414a6…78a35cd6ed`, COM 14 `6f1ad05a…c4c6dbdc9`; both match configuration |
| Result-set count and order per version | Statement-by-statement comparison against each shipped body | 3 for Commerce 15, 4 for Commerce 14, order preserved in both |
| The two versions cannot cross-match | Hashes differ; resolution simulated with each body deployed | Each database resolves only its own version's entry |
| Catalog loading against real data | — | **Not done.** Field verification pending. |

## Before enabling

Confirm which Commerce version each target database is actually running, and deploy only
that script. Over-deploying both is harmless — the hash gate makes the wrong one inert — but
knowing which one you expect to fire is what makes the Profiler trace readable.

During field verification, exercise the **variation** response group specifically. That is
the branch the extra Commerce 14 result set lives in, and the other branches will not
exercise the difference this split exists for.

## Rollback

Set `"enabled": false` on OPT-0005 (or OPT-0014). The next call goes to Optimizely's
procedure.

Note the coupling: if [OPT-0008 / OPT-0015](OPT-0008-catalog-entry-list.md) is armed, its
replacement calls *this* replacement directly, in the database, where no configuration gate
applies. Disabling this entry does not un-wire that call. Drop the object only after the
list-loader redirect is off as well — otherwise the list loader keeps calling an object that
no longer exists. Leaving the object in place while disabling the entry is always safe.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| Result-set parity per Commerce version | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
