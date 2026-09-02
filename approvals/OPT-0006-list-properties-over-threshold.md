# OPT-0006 — GetListPropertiesOverThreshold

| | |
| --- | --- |
| **Entry** | *(none — this object has no redirect)* |
| **Object created** | `dbo.GetListPropertiesOverThreshold_optiperf_v1` |
| **Derived from** | EPiServer.Cms.Core 12.24.1 |
| **Risk** | Moderate |
| **Status** | Catalogued — reachable only from OPT-0002, which is disarmed |
| **Script** | [`sql/GetListPropertiesOverThreshold_optiperf_v1.sql`](../sql/GetListPropertiesOverThreshold_optiperf_v1.sql) |
| **Caller** | [OPT-0002](OPT-0002-net-content-list-paged.md) |
| **Reviewed** | 2026-09-02 |

## Why there is no configuration entry

This is a table-valued function, and no CMS client ever calls it directly. It is reachable
only from inside `netContentListPaged`. There is no client statement to intercept, so there
is nothing for a redirect to rewrite — the switch happens inside
`netContentListPaged_optiperf_v1`, which names the versioned copy in its `FROM` clause.

It exists as a separate object because the original is a separate object. Inlining it into
the caller would have made the caller's diff against the shipped body unreadable, which is
the one thing the deploy-beside design is protecting.

It is nevertheless the riskiest object in the catalogue, which is why it has a record of its
own rather than a paragraph in OPT-0002's.

## What the original computes

Given a set of content items and a size threshold, it collects the scope names of oversized
list properties and reduces them to the *prefix-minimal* set: a scope is kept unless some
other kept scope is a prefix of it.

It does that with a `WHILE` loop. For each candidate, shortest first, it scans the
accumulated results looking for a prefix match. The accumulator is an unindexed table
variable, so every scan is a full scan and the loop is quadratic in the number of oversized
list properties on the page. The only content that reaches this code at all is content with
a large nested block list — everything else is filtered out by the threshold — so the
quadratic term bites exactly where the pages are already heaviest.

## The change

The loop becomes one `NOT EXISTS`, over an accumulator that is now indexed on
`(ContentID, ScopeName)` because it is probed rather than scanned.

The reduction is sound:

- Candidates are processed shortest-first, and a candidate is dropped only when an
  already-kept scope is a prefix of it.
- Suppose scope `T` prefixes candidate `S`, and `T` was itself dropped. `T` can only have
  been dropped because some kept scope `U` prefixes `T`. Prefix is transitive, so `U`
  prefixes `S`, and `S` is dropped either way.
- So "dropped because a *kept* scope prefixes it" and "dropped because *any* shorter scope
  prefixes it" select the same set. The accumulator is not load-bearing and the loop is not
  needed.

Exact-length duplicates were handled by the original's own `LIKE` matching; here they are
handled by `SELECT DISTINCT`.

## The non-equivalence

One case does not hold, and it is the reason for the Moderate rating.

The prefix test is `LIKE`, not a substring comparison, so wildcard characters *inside a
scope name* are interpreted as wildcards. A scope name containing an underscore — legal,
and not unheard of in a property name — can `LIKE`-match a different scope of the same
length: `'a_c'` matches `'abc'`.

The original walks same-length candidates in insertion order and keeps whichever it reaches
first, dropping the other. This version keeps both, because its `NOT EXISTS` considers only
*strictly shorter* scopes and therefore cannot break a same-length tie at all.

The consequence, downstream in `netContentListPaged`: a possible extra row in the delayed
scope set for content that has two equal-length sibling scopes where one `LIKE`-matches the
other, and that extra row can suppress a `LongString` the original would have returned.

That is a behavioural difference on real data, not a plan difference. It is what separates
this from every Low-rated entry in the catalogue.

`%` and `[` have the same problem for the same reason. `_` is called out because it is the
one that occurs in practice.

## Why it was not fixed

Preserving the tie-break exactly means preserving an ordering dependency, and preserving an
ordering dependency means keeping the loop — which is the thing being removed. `ESCAPE`ing
the pattern operand does not help either: it would change the *original's* semantics as
well as this one's, since the original's matches on wildcard-bearing names are part of the
behaviour being reproduced.

So the difference is accepted and recorded rather than fixed, and the cost of that decision
is pushed to the gate: sites with underscores in list property scope names must not enable
OPT-0002.

## What could make this wrong

- **A `_`, `%` or `[` in `tblContentProperty.ScopeName` for a row with a non-null
  `ListIndex`.** This is the case above, and it is directly checkable per database. See
  OPT-0002's pre-enable checklist.
- **A CMS release changing the original function.** Not gated here, because there is no
  redirect to gate. It is gated one level up: OPT-0002 pins `netContentListPaged`'s body
  hash, and a CMS release that reworked the delayed-scope logic would almost certainly
  touch the caller too. This is weaker than a direct hash gate and is worth knowing about.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Script is valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| Transitivity argument | Reviewed by hand against the original loop | Holds for all non-wildcard scope names |
| Wildcard divergence | Constructed by hand: `'abc'` and `'a_c'` at equal length | Reproduces; original keeps one, replacement keeps both |
| Behaviour on real content with nested block lists | — | **Not done.** Blocked behind OPT-0002 field verification. |

## Rollback

There is no switch of its own. Turning off OPT-0002 makes this object unreachable, since
nothing else in the database names it. It can then be dropped or left; a site running
Optimizely's procedures never executes it either way.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| Accepted the wildcard non-equivalence | | |
