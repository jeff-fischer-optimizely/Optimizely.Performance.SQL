# OPT-0001 — ecf_OrderSearch

| | |
| --- | --- |
| **Entry** | OPT-0001 |
| **Object created** | `dbo.ecf_OrderSearch_optiperf_v1` |
| **Replaces the call to** | `dbo.ecf_OrderSearch` |
| **Derived from** | EPiServer.Commerce.Core 15.1.0 / 14.46.0 — identical body in both |
| **Risk** | Moderate |
| **Status** | Catalogued (`enabled: false`) |
| **Script** | [`sql/ecf_OrderSearch_optiperf_v1.sql`](../sql/ecf_OrderSearch_optiperf_v1.sql) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved as written, and deliberately left disarmed.

This is the largest single item in the fleet survey: 75 databases, and roughly an eighth
of the costed-query corpus by logical reads, attributed to one procedure. Every order search in Commerce
reaches it, through `ecf_Search_PurchaseOrder`, `ecf_Search_ShoppingCart` and
`ecf_Search_PaymentPlan`. It is the entry with the most to gain and the one whose plan
behaviour we can say the least about from the source, which is exactly the combination
that should be measured rather than switched on.

## The change

One clause. The inner join to a materialised `DISTINCT` over the union of every order
metaclass table becomes a correlated `EXISTS`:

```sql
-- was
INNER JOIN (select distinct U.[Key] from ( <union> ) U) META
    ON OrderGroup.[OrderGroupId] = META.[Key]

-- is
WHERE EXISTS (SELECT 1 FROM ( <union> ) U WHERE U.[Key] = OrderGroup.OrderGroupId)
```

The metaclass cursor, the union text it builds, the caller's filter, the `ORDER BY`, the
paging clause and the count query are all untouched.

## Equivalence

The result set is unchanged for every input, and the argument closes:

1. Nothing in the outer query projects a column of `META`. The select list is
   `OrderGroupId`; the count query is `COUNT(1)`. The join contributes filtering only.
2. The derived table is `DISTINCT` on the single joined column, so an inner join to it
   matches each `OrderGroup` row at most once — it can neither duplicate nor drop rows
   relative to an existence test.
3. Therefore both forms select the same rows of `OrderGroup` with the same multiplicity.

This does not depend on the contents of `@SQLClause`, `@MetaSQLClause` or `@OrderBy`. Those
strings are concatenated into the same positions in both forms, so whatever they do, they
do identically to each.

## What could make this wrong

Stated so it can be tested rather than argued about:

- **The claim fails if the outer query ever projects from the derived table.** It does not
  today, in either Commerce version. If a future Commerce release adds a metaclass column
  to the select list, the hash gate catches the body change and the redirect stops firing
  before anyone has to notice this paragraph.
- **The plan can be worse.** The result-set argument holds universally; the plan argument
  does not. A semi-join lets the caller's filter run before the metaclass probe, which is
  the right default when the filter is selective. A filter matching most of `OrderGroup`
  turns it into a per-row probe that can cost more than the one-time materialisation it
  replaced. Nothing in the source bounds this, because the filter arrives as text at
  runtime.

That second point is the whole reason for the Moderate rating and for the disarmed state.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Script is valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Body hash pins the reviewed original | Recomputed from the shipped package | `630869a7…21bd4fc`, matches configuration |
| Commerce 14 and 15 bodies agree | Byte comparison of the two shipped scripts | Identical, so one entry covers both |
| Plan behaviour under a real workload | — | **Not done.** This is what shadow mode is for. |

## Before enabling

1. Run in shadow mode against the store with the largest `OrderGroup` in the estate. That
   is where a semi-join regression would show first and where the win, if it is real, is
   largest.
2. Compare both a selective search (one order number) and an unselective one (a broad date
   range returning most of the table). The second is the case the Moderate rating is about.
3. Exercise `@ReturnTotalCount = 1`, which runs the same shape a second time.
4. Confirm paging: `@StartingRec`/`@NumRecords` past the end of the result set, and the
   `@RecordCount` output parameter.

Only then flip `enabled` to `true`, and only for the Commerce estate the measurement
covered.

## Rollback

Set `"enabled": false` on OPT-0001 in `config/approved-sql.json`. The next call goes to
Optimizely's procedure. Nothing has to be restored, because nothing was overwritten.

`dbo.ecf_OrderSearch_optiperf_v1` may be left in place; it is unreachable once the redirect
is off, since nothing but the shim ever names it.

## Not fixed here

`@SQLClause`, `@MetaSQLClause` and `@OrderBy` are concatenated into the executed statement
unparameterised. Two consequences, both real:

- **Plan cache pressure.** Every distinct search text is a distinct statement and gets its
  own cache entry. On a busy store this is a meaningful share of the cache churn, and it is
  independent of anything this rewrite does.
- **An injection surface.** The values reach `sp_executesql` as literal text. Whether that
  is exploitable depends on what the calling assembly puts in them, which is not visible
  from the database.

Neither is fixable in a procedure body. Parameterising them means changing the contract
with the calling assembly, and that assembly is Optimizely's. Reported upstream; recorded
here so that a later reader does not mistake the omission for an oversight.

Deliberately *not* worked around by, for example, sanitising the clauses in the replacement
procedure. A silent difference in what a search accepts would be a behaviour change wearing
a performance rewrite's clothes, and it would be discovered by a customer rather than by us.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
