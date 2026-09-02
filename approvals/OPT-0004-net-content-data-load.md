# OPT-0004 / OPT-0012 — netContentDataLoad

| | |
| --- | --- |
| **Entries** | OPT-0004 (CMS 12), OPT-0012 (CMS 11) |
| **Objects created** | `dbo.netContentDataLoad_optiperf_v1`, `dbo.netContentDataLoad_cms11_optiperf_v1` |
| **Replace the call to** | `dbo.netContentDataLoad` |
| **Derived from** | EPiServer.Cms.Core 12.24.1 and 11.21.5 |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`), unconditional |
| **Scripts** | [`netContentDataLoad_optiperf_v1.sql`](../sql/netContentDataLoad_optiperf_v1.sql), [`netContentDataLoad_cms11_optiperf_v1.sql`](../sql/netContentDataLoad_cms11_optiperf_v1.sql) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed. This is the only rewrite in the catalogue that ships unconditionally,
because it is the only one that depends on nothing — no collation, no index, no
compatibility level, no optimiser behaviour. It is arithmetic on statement count.

## The change

The original reads `tblContent` for the same `@ContentID` three times in its prologue,
before the main select:

```
1.  SELECT @ContentTypeID    = fkContentTypeID          WHERE pkID = @ContentID
2.  SELECT @LanguageBranchID = fkMasterLanguageBranchID WHERE pkID = @ContentID   (conditional)
3.  SELECT @MasterLanguageID = fkMasterLanguageBranchID WHERE pkID = @ContentID
```

Three clustered index seeks to fetch two columns of one row. Folded into one seek plus
assignments from local variables:

```sql
SELECT
    @ContentTypeID    = tblContent.fkContentTypeID,
    @MasterLanguageID = tblContent.fkMasterLanguageBranchID
FROM tblContent
WHERE tblContent.pkID = @ContentID

IF (@MasterLanguageID IS NOT NULL
    AND (@LanguageBranchID = -1
        OR NOT EXISTS (SELECT 1 FROM tblContentLanguage
                       WHERE fkContentID = @ContentID
                         AND fkLanguageBranchID = @LanguageBranchID)))
    SET @LanguageBranchID = @MasterLanguageID
```

Nothing else is touched. The five result sets are the statements Optimizely wrote.

## Why it is worth doing at all

Per call this saves two trivial seeks, which is not interesting on its own. The multiplier
is what makes it interesting: this is the most widely deployed procedure in the surveyed
estate — 361 databases, essentially all of them — and the survey window recorded it in the
billions of executions.

Two saved seeks per call at that volume is a large absolute number made of individually
negligible pieces. It is exactly the kind of cost that never gets attention, because no
single execution looks bad in a trace and there is nothing to point at in a slow-query
report.

## Equivalence, and the one part that carries weight

Almost all of this is trivially safe: one statement folded into another, reading the same
two columns of the same row, with no join, predicate or projection altered.

The load-bearing part is the `@MasterLanguageID IS NOT NULL` guard.

In the original, when `@ContentID` matches no row, statement 2 assigns nothing and
`@LanguageBranchID` keeps the caller's value. A naive fold to `SET @LanguageBranchID =
@MasterLanguageID` would set it to `NULL` instead — a real difference. The guard makes the
assignment conditional on a row having been found, which reproduces the original exactly.

In that no-row case the procedure returns no rows and `RETURN 0` regardless, so the
difference is not observable from outside *today*. The guard is there anyway, because
"unobservable" is a claim about the current callers and the guard costs nothing. A future
caller that inspects the output parameter would otherwise find a `NULL` that Optimizely's
procedure never produces.

`@ContentTypeID` is assigned and never read, in the original and here. It is retained so
that a reader diffing the two bodies is not left wondering whether removing it mattered.

## CMS 11 and CMS 12

Two scripts, two hashes, one decision.

The bodies differ by six lines, all in the property load, where CMS 12 added
`BranchSpecificScope` handling. **The prologue this rewrite touches is character for
character identical between them**, so the argument above carries over to OPT-0012 without
amendment — which is why one record covers both rather than two records that could drift.

The CMS 11 copy reproduces the rest of the CMS 11 body unchanged, including its
`tblProperty` / `tblPageDefinition` naming. No CMS 12 logic was carried backwards.

## What could make this wrong

- **The guard being dropped in a future revision.** It looks redundant and is not. It is
  called out in the body comment for that reason.
- **A CMS release changing the prologue.** Caught by the body hash: the redirect stops
  firing and Optimizely's procedure runs. This is the ordinary fail-safe path, not a
  failure mode needing attention.

There is no third item. This entry has no gates because there is nothing to gate on.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Both scripts are valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hashes pin the reviewed originals | Recomputed from both shipped packages | CMS 12 `3c6f9149…3331f1`, CMS 11 `40cdd22a…71c1ee`; both match configuration |
| The two prologues really are identical | Byte comparison of the shipped bodies | Identical |
| Version routing | Resolution simulated under both CMS hosts | CMS 11 host selects the cms11 copy, CMS 12 host the other |
| Application behaviour | — | **Not done.** Field verification pending. |

## Before enabling

Nothing conditional to check, which is unusual here and worth stating plainly. Deploy the
script for the CMS version you run and the redirect applies.

Worth exercising once during field verification, because they are the paths the guard
protects:

- a content id that does not exist;
- `@LanguageBranchID = -1`;
- a language branch that exists but has no row in `tblContentLanguage` for the content.

## Rollback

Set `"enabled": false` on OPT-0004 (or OPT-0012). The next call goes to Optimizely's
procedure.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
