# Query triage — first round

The record of what was examined, what was rewritten, and what was deliberately left alone.

`config/approved-sql.json` says what the catalogue *does*. This document says what it was
drawn from, and — more usefully — what it decided not to do. A catalogue that only lists its
own contents invites the same query to be re-diagnosed every six months by someone who has
no way of knowing it was looked at before.

**Date:** 2026-09-02
**Corpus:** 165 costed statements — 82 CMS, 83 Commerce
**Fleet survey:** production sweep of 2026-08-12, 690 of 691 live production databases

## What is deliberately not here

**No cost figures.** The source spreadsheets carry monthly and annual money columns. They
are not reproduced, quoted or derived from anywhere in this repository. Impact is expressed
as share of corpus logical reads, execution counts and database counts.

**No customer identification.** The survey's raw rows name customers, servers and
subscriptions. Those files live outside the working tree, in
`Documents/Optimizely-Performance-SQL-survey/`, and are covered by `.gitignore` along with
the intermediate matching artefacts (`.procsrc/`, `.procs.json`, `.matched.json`).

**No query text from the 61 non-Optimizely statements.** Their text identifies the
customer or the add-on that wrote them. They are summarised in aggregate only, in
[the section below](#the-61-statements-that-are-not-optimizelys).

**A note on the denominator.** Percentages below are shares of *the corpus* — the 165
costed statements — by logical reads. They are not shares of all reads in the fleet. The
corpus is the poorly-performing tail, not the whole workload, so "13% of corpus" is a much
smaller share of everything the fleet does.

## The corpus, and how it splits

| | Statements | Share of corpus reads |
| --- | ---: | ---: |
| CMS | 82 | 67.4% |
| Commerce | 83 | 32.6% |

Each statement was matched against the procedure bodies Optimizely ships, across four
packages: `EPiServer.Cms.Core` 11.21.5 and 12.24.1, `EPiServer.Commerce.Core` 14.46.0 and
15.1.0.

| | Statements | Share of corpus reads |
| --- | ---: | ---: |
| Matched to a shipped Optimizely procedure | **104** | **65.1%** |
| Not matched — customer or add-on SQL | 61 | 34.9% |

Within CMS, 55 of 82 statements matched (60.8% of CMS reads). Within Commerce, 49 of 83
(74.1% of Commerce reads).

**That 65.1% is the ceiling on what a shipped catalogue can ever address.** Everything
below is about how much of that ceiling this round reached.

## What this round addresses

Twelve procedures, 40 of the 165 statements, **33.9% of corpus logical reads** — which is
52% of the matched portion.

| Procedure | Share | Statements | Databases | Entries | Risk |
| --- | ---: | ---: | ---: | --- | --- |
| `ecf_OrderSearch` *(via `ecf_Search_PurchaseOrder`, `_ShoppingCart`, `_PaymentPlan`)* | 13.05% | 1 | 75 | OPT-0001 | Moderate |
| `netContentListPaged` | 9.91% | 12 | 185 | OPT-0002 (CMS 12), OPT-0010 / OPT-0011 (CMS 11) | Moderate / Low |
| `editDeletePageCheckInternal` | 5.24% | 6 | 95 | OPT-0003, OPT-0013 | Low |
| `netContentDataLoad` | 1.96% *(+0.88% shared)* | 7 | 361 | OPT-0004, OPT-0012 | Low |
| `netContentChildrenReferences` | 0.90% | 2 | 139 | OPT-0009 | Low |
| `ecf_CatalogEntry_Components` | 0.83% | 3 | 100 | OPT-0005, OPT-0014 | Low |
| `ecf_NodeEntryRelations` | 0.26% | 1 | 108 | OPT-0007 | Low |
| `ecf_CatalogEntry_List` | 0.15% *(+0.44% shared)* | 3 | 100 | OPT-0008, OPT-0015 | Low |
| `GetListPropertiesOverThreshold` | 0.15% | 1 | 96 | OPT-0006 *(callee only)* | Moderate |

"Shared" rows are statements the matcher attributed to more than one procedure; they are
counted once in the 33.9% total.

Two of the largest — OPT-0001 and OPT-0002 — are catalogued with `enabled: false`, gated
behind shadow-mode measurement and a per-database scope-name check respectively.

That leaves about 11% of the corpus armed, plus the CMS 11 share of `netContentListPaged`
(OPT-0010 / OPT-0011, which *are* armed). The corpus attributes reads per statement, not per
CMS version, so that share cannot be separated out here; `netContentListPaged` appears on 99
CMS 11 databases out of the 185 that have it. See [`approvals/`](../approvals/README.md).

## The cost classes

Five patterns account for nearly everything that was rewritable. Naming them matters more
than the individual entries, because the next batch of queries will be more of the same.

### 1. Table-variable cardinality below compatibility level 150 — the big one

Below level 150, SQL Server compiles a table variable with a fixed estimate of one row. The
optimiser then picks nested-loop plans sized for one row against inputs holding hundreds.
Table-valued parameters are table variables, so this reaches every batch loader in both
products.

**55.9% of the production fleet is below level 150.** Weighted by attributed cost for this
class specifically, it is 64.6%.

This is the class that produced most of the catalogue: OPT-0002, OPT-0003, OPT-0005,
OPT-0007, OPT-0008, OPT-0011, OPT-0013, OPT-0014, OPT-0015. The remedy is
`OPTION (RECOMPILE)`, gated by a compatibility *ceiling* — at 150 and above the engine
defers compilation itself and the hint becomes pure compile cost.

It has a competitor, and the competitor is better: see
[the configuration change](#the-change-that-competes-with-all-of-this).

### 2. Per-row loops doing set-based work

CMS 11 unpacks a `VARBINARY(8000)` id list one `INSERT` per four bytes. That single
statement is the highest-execution item in the entire survey — 7.6 billion executions across
99 databases. Replaced with a tally-CTE unpack (OPT-0010 / OPT-0011).

`GetListPropertiesOverThreshold` reduces scope names to a prefix-minimal set with a `WHILE`
loop over an unindexed accumulator, quadratic in the number of oversized list properties.
Replaced with one `NOT EXISTS` (OPT-0006).

### 3. Redundant single-row seeks

`netContentDataLoad` seeks `tblContent` by primary key up to three times to read two columns
of one row, in the most widely deployed procedure in the estate — 361 databases, billions of
executions. Folded into one seek (OPT-0004 / OPT-0012).

Individually negligible, which is exactly why it survives: no single execution looks bad in
a trace and there is nothing to point at in a slow-query report.

### 4. Uncorrelated derived tables

`ecf_OrderSearch` joins to a materialised `DISTINCT` over the union of every order metaclass
table, so search cost scales with total order history rather than with the number of
matching orders. Turned into a semi-join (OPT-0001).

### 5. Non-sargable predicates — the class that did not exist

The working hypothesis at the start was that a whole low-risk tier lived here: check the
collation, strip `LOWER()` from predicates, ship it.

Every `LOWER()` in all four shipped packages was examined. Nearly all are scalar assignments
evaluated once, or predicates against `LoweredUserName` / `LoweredRoleName` — columns
Optimizely denormalises precisely to keep the predicate sargable, and therefore already
optimal. **Exactly two column-side `LOWER()` predicates exist across all four packages**:
one in `netContentChildrenReferences`, and one in a Commerce metaclass DDL procedure that
runs at deployment time.

The tier turned out to be a single query against a table holding one row per configured
language — a dozen rows, one page. The payoff is negligible and OPT-0009 says so in its own
header rather than claiming otherwise.

This is recorded at length because a hypothesis that did not pay off is still a result, and
the next person to have the idea should be able to find out it was already tested.

## Examined and deliberately not rewritten

Matched to a shipped procedure, diagnosed, and left alone. Together these are 18.4% of the
corpus.

### `netPropertySearchValueMeta` — 7.01%, 46 databases

The largest single thing this catalogue does not touch, and the decision is not close.

It builds a property-search predicate as dynamic SQL — correctly parameterised through
`sp_executesql`, with a branch per searchable property — and runs it against
`tblPageLanguage` joined to `tblTree` and `tblContent`. There is no inefficiency to remove.
The cost is what property search over the content tree costs in a relational schema, and it
is proportional to the subtree being searched.

**Not a rewrite target. The answer is Search & Navigation**, or not doing tree-wide property
searches at request time. A SQL-level change here would be rearranging the cost, not
removing it.

### `netPageListAll` — 3.76%, 168 databases

Two branches. With no `@PageID` it selects every row of `tblPage`; with one, every
descendant from `tblTree` ordered by nesting level. Both are minimal statements — there is
no predicate to make sargable and no join to reorder.

The reads are large because the caller asked for the whole tree. That is a caller-side
problem and it cannot be fixed underneath the caller.

### `netContentMatchSegment` — 1.90%, 223 databases

A two-table join with equality predicates on `fkParentID` and `URLSegment`. Nothing to
rewrite; the statement is already the statement you would write.

What would help is an index on `tblContentLanguage (URLSegment)`. That is a schema change to
a table Optimizely ships, and it is outside this shim's remit for the same reason modifying
a shipped procedure is: it is Optimizely's object, and an upgrade has every right to have
opinions about it. **Recorded as an index recommendation, not taken as a rewrite.**

### `ecf_Catalog_GetChildrenEntries` — 2.01%, 24 databases

An anti-join over the whole catalog — every entry with no primary node relation — sorted by
name. The `NOT EXISTS` is already the right shape. The reads scale with catalog size because
the result does.

### `netQuickSearchByExternalUrl` — 2.82%, 53 databases

A URL-matching search. Not individually diagnosed this round; the body was not extracted.
Carried forward as unexamined rather than cleared — see below.

### The tail

`netActivityLogAssociatedAllList`, `netPageTypeGetUsage`, `ecf_Inventory_QueryInventory`,
`CatalogItemChange_Insert`, `netSoftLinksGetBroken`, `netMappedIdentityGetById`,
`ecf_Search_OrderGroup`, `ecf_CatalogItem_AssetKey`, `ecf_Pricing_*`,
`CatalogContentProperty_*`, `ecf_Guid_FindEntity`, `mdpsp_*` and others — each below 1.2% of
the corpus.

These were not individually diagnosed. Below roughly a percent, the effort of deriving a
versioned copy, arguing its equivalence and carrying it through a change window is not
obviously repaid, and the honest statement is that the round stopped rather than that these
were cleared.

## Candidates for a second round

Genuine opportunities identified and not taken. Recorded so the next round starts here
rather than re-deriving them.

| Candidate | Share | Databases | Why it is a candidate | Why not this round |
| --- | ---: | ---: | --- | --- |
| `netMappedIdentityGetByGuid` | 0.96% | 204 | A single join on a `GuidParameterTable` TVP, executed 2.35 billion times. Cost class 1 exactly, and the smallest possible instance of it. | Missed in the first pass; it is the clearest remaining Low-risk item and should lead the next round. |
| `netVersionFilterList` | 1.04% | 165 | Two `IDTable` TVPs, counted and branched on, feeding several paging branches. | 235 lines with many branches. High effort per statement, and each branch needs its own equivalence argument. |
| `ecf_CatalogEntry` | 1.11% | 109 | An `EXISTS` probe on `CatalogEntry` immediately followed by a select from the same row — the OPT-0004 fold, in Commerce. | Small share, and the fold interacts with an early `RETURN` that changes the result-set count. Needs more care than OPT-0004 did. |
| `netContentListPaged` `#temp` variant | — | 185 | Replacing `@ContentItems` with a `#temp` table fixes the estimate without a per-call compile and carries real column statistics. | Deferred to OPT-0002 v2. Changes tempdb behaviour and transaction scope; cannot be argued correct by inspection, so it belongs behind a measurement rather than in front of one. |
| `netQuickSearchByExternalUrl` | 2.82% | 53 | Not yet examined. | Body not extracted this round. |

## The 61 statements that are not Optimizely's

34.9% of the corpus — a third of the measured problem — is SQL that Optimizely did not
write. Customer application queries and third-party add-ons.

Reported in aggregate, with no text, because their text identifies who wrote them:

| | |
| --- | --- |
| Statements | 61 (27 CMS-hosted, 34 Commerce-hosted) |
| Share of corpus reads | 34.9% |
| Median databases per statement | 1 |
| Statements appearing on exactly one database | 39 of 61 |
| Widest spread | one statement on 165 databases |

The shape is the important part. **Two thirds of these statements exist on a single
database**, which means they are bespoke: one customer's query, or one add-on installed by
one customer. A shipped catalogue of approved rewrites is structurally the wrong instrument
for them — there is nothing to ship, because there is no shared body to rewrite.

The handful with wide spread are the exception, and they are the ones worth pursuing: a
statement appearing on 165 databases is an add-on that many customers install, and an
add-on's author can be told. That is an upstream conversation, not a catalogue entry.

**What this means for expectations:** even a perfect catalogue leaves a third of the
measured problem untouched. Anyone sizing this work from the corpus total will be
disappointed by a third for reasons that have nothing to do with how well the rewrites
perform.

## The change that competes with all of this

Most of the Low-risk catalogue exists to work around the absence of deferred table-variable
compilation below compatibility level 150.

`ALTER DATABASE [epicms] SET COMPATIBILITY_LEVEL = 150` fixes that class outright, for every
statement in it, including the ones this catalogue does not cover and the customer SQL it
structurally cannot cover. It is a configuration change, not a code change, and it does not
need the shim at all.

Worth stating plainly, because it is the honest framing of cost class 1: **for the majority
of the fleet, the compatibility-level raise is the better fix and the rewrites are the
fallback for databases that cannot take it.** The shim's durable value is in the classes a
level raise does not touch — the per-row loops, the redundant seeks, the derived-table
shape — and in the databases where a level raise is blocked.

The fleet survey sizes the raise in two tranches (levels 120–140 can take it directly;
100–110 need `LEGACY_CARDINALITY_ESTIMATION = ON` alongside, to avoid taking the cardinality
estimator swap at the same time). The step-by-step procedure, the per-database eligibility
query and the post-change verification query are in
[docs/compatibility-level-remediation.md](compatibility-level-remediation.md). The costing
behind the tranches is in the survey documents outside this repository.

## Method

1. **Corpus.** Costed query lists per workload, one row per statement, carrying logical
   reads, execution count and the databases the statement appears on.
2. **Shipped bodies.** Procedure and function bodies extracted from the `tools/*.sql` scripts
   in the four NuGet packages.
3. **Matching.** Each corpus statement matched against those bodies. A statement can match
   several procedures when they share a statement — those rows are attributed to all matches
   and counted once in totals.
4. **Diagnosis.** Matched statements read against their surrounding procedure, since the
   fix usually depends on context the statement alone does not show — which table variable
   it joins, what the caller passes, which `@@ROWCOUNT` test follows it.
5. **Rewrite and gate.** Where a fix existed, a versioned copy was derived, its equivalence
   argued in the script header, and its preconditions chosen so it engages only where the
   argument holds.
6. **Verification.** All fifteen scripts parse-checked against a live SQL Server with
   `SET PARSEONLY ON`; body hashes recomputed from the shipped packages; resolution
   simulated across database shapes; the highest-risk rewrite differential-tested against
   its original.

Steps 1–3 produce intermediate files (`.procsrc/`, `.procs.json`, `.matched.json`) that are
gitignored: they carry customer-identifying data.

### What has not been done

**None of this has been verified against a running application.** The acceptance test is
Optimizely Foundation, both CMS versions, SQL Profiler attached, confirming the optimised
text reaches the server and the application behaves unchanged. Until that has been done,
every entry is approved to *test*, not approved to *ship*. The unit suites and the parse
checks are necessary and are not sufficient.
