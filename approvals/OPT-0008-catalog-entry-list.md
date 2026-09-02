# OPT-0008 / OPT-0015 — ecf_CatalogEntry_List

| | |
| --- | --- |
| **Entries** | OPT-0008 (Commerce 15), OPT-0015 (Commerce 14) |
| **Objects created** | `dbo.ecf_CatalogEntry_List_optiperf_v1`, `dbo.ecf_CatalogEntry_List_com14_optiperf_v1` |
| **Replace the call to** | `dbo.ecf_CatalogEntry_List` |
| **Derived from** | EPiServer.Commerce.Core 15.1.0 and 14.46.0 — **byte-identical bodies** |
| **Risk** | Low |
| **Status** | Armed (`enabled: true`) |
| **Scripts** | [`ecf_CatalogEntry_List_optiperf_v1.sql`](../sql/ecf_CatalogEntry_List_optiperf_v1.sql), [`ecf_CatalogEntry_List_com14_optiperf_v1.sql`](../sql/ecf_CatalogEntry_List_com14_optiperf_v1.sql) |
| **Depends on** | [OPT-0005 / OPT-0014](OPT-0005-catalog-entry-components.md) |
| **Reviewed** | 2026-09-02 |

## Decision

Approved and armed. The performance change is routine; the interesting question is how two
entries are told apart when the procedure they replace is identical in both product
versions.

## The change

`OPTION (RECOMPILE)` on the four statements joining the `@CatalogEntries` table-valued
parameter — one of which copies it into a second table variable before handing that to the
components procedure — and the call to `ecf_CatalogEntry_Components` redirected to the
versioned copy.

No join, predicate or projection altered. The `ORDER BY` on the first statement is the
original's.

Below compatibility level 150 all four statements are planned for a single row, and the
copy-into-a-second-table-variable step compounds it: the mis-estimate is carried into the
callee as well.

## Equivalence

Recompile directives cannot change which rows a statement returns. The redirected call goes
to a body whose own equivalence argument is recorded in
[OPT-0005](OPT-0005-catalog-entry-components.md), and that argument is likewise hints-only —
so, unlike [OPT-0002](OPT-0002-net-content-list-paged.md), this caller inherits nothing that
would raise its tier.

## The discrimination problem

`ecf_CatalogEntry_List` is byte-identical in Commerce 14 and Commerce 15. Its own
`originalBodyHash` therefore cannot say which version a database is running — both entries
carry the same hash, `2ff73cbe…f35e360c66`, and both would match.

That matters because the two replacements are *not* interchangeable. Each calls its own
version's components copy, and those differ: the Commerce 14 one returns an extra `Merchant`
result set. Pick the wrong one and the components call returns the wrong number of result
sets.

The discriminator is the callee:

```json
"requiredProcedureBodies": {
  "ecf_CatalogEntry_Components": "5eb414a6…78a35cd6ed"   // OPT-0008: the Commerce 15 body
}
```

```json
"requiredProcedureBodies": {
  "ecf_CatalogEntry_Components": "6f1ad05a…c4c6dbdc9"   // OPT-0015: the Commerce 14 body
}
```

`ecf_CatalogEntry_Components` hashes differently in the two versions, so exactly one of
those conditions can hold on any database. **The pair is mutually exclusive by construction,
not by ordering** — there is no configuration order, no precedence rule and no "first match
wins" behaviour to get right. That property is what made this acceptable; a scheme that
depended on entry order would have been rejected, because entry order is exactly the kind of
thing a later edit reshuffles without noticing.

Both entries additionally require their components *replacement* to be deployed
(`requiredProcedures`), which is a separate concern: see below.

## Why the dependency gate exists

An earlier draft had these replacements calling `ecf_CatalogEntry_Components_optiperf_v1`
with nothing verifying it had been deployed. The redirect would pass every gate, rewrite the
call, and then fail at runtime on a missing object — a hard error in the catalog loader,
caused by the shim, on a database where Optimizely's own procedures were all present and
working.

That is the worst failure shape available to this design: the fallback path exists precisely
so that an incomplete deployment is inert, and this bypassed it.

`requiredProcedures` closes it. Until the callee exists, the redirect is skipped and
Optimizely's procedure keeps running. A half-finished deployment is now inert rather than
broken, which is the property the whole approach rests on.

## Gates, and why each one

| Gate | Reason |
| --- | --- |
| `maximumCompatibilityLevel: 140` | Above that the engine defers table-variable compilation itself. |
| `requiredProcedures` | The replacement calls the versioned components copy. Without this the redirect fails at runtime rather than falling back. |
| `requiredProcedureBodies` | Discriminates Commerce 14 from Commerce 15, since this procedure's own hash cannot. |
| `originalBodyHash` | Pins the reviewed body. Identical across both versions, hence the above. |

## What could make this wrong

- **A Commerce release that changes `ecf_CatalogEntry_Components` without changing
  `ecf_CatalogEntry_List`.** Both entries then fail their `requiredProcedureBodies` check
  and both are skipped. That is the correct outcome — the discriminator has genuinely stopped
  being able to tell the versions apart, and refusing to guess is right — but the reported
  reason will be `PreconditionsNotMet`, which does not say *why*. Worth knowing before
  debugging it.
- **A Commerce release that makes the two versions' `ecf_CatalogEntry_List` bodies differ.**
  Then the hash gate does the work on its own and the callee discrimination becomes
  redundant rather than wrong. No action needed.
- **Deploying the components replacement for the wrong Commerce version.** Its own hash gate
  stops the components redirect from firing, and the `requiredProcedureBodies` check here
  stops the list redirect too. Inert, as intended.

## Evidence

| Checked | How | Result |
| --- | --- | --- |
| Both scripts are valid T-SQL | `SET PARSEONLY ON` against a live SQL Server | Clean |
| The two shipped bodies really are byte-identical | Byte comparison across the two packages | Identical — which is why the callee discriminator is needed |
| Body hash pins the reviewed original | Recomputed from the shipped packages | `2ff73cbe…f35e360c66`, matches both entries |
| The pair is mutually exclusive | Resolution simulated with each Commerce version's components body deployed | Commerce 14 shape selects OPT-0015, Commerce 15 shape selects OPT-0008; never both |
| Missing callee falls back rather than failing | Unit test with the components replacement absent | Redirect suppressed, Optimizely's procedure runs |
| Catalog loading against real data | — | **Not done.** Field verification pending. |

## Before enabling

1. Deploy the components replacement for your Commerce version **first**. Wrong order is
   inert, not broken, but it wastes a change window.
2. Confirm in the Profiler trace that the *expected* variant fired. Because the two entries
   are distinguished by a callee hash rather than by anything visible in the call, the trace
   is the cheapest place to confirm the discrimination worked as designed.
3. Exercise the variation response group, for the reason given in
   [OPT-0005](OPT-0005-catalog-entry-components.md).

## Rollback

Set `"enabled": false` on OPT-0008 (or OPT-0015). The next call goes to Optimizely's
procedure.

Turn this off **before** dropping the components replacement, not after: the call from this
replacement to that one lives in the database and no configuration gate stands between them.

## Sign-off

| Role | Name | Date |
| --- | --- | --- |
| Author | | |
| Reviewer | | |
| Verified the correct variant fires (Profiler) | | |
| DBA (deployment) | | |
| Field verification (Foundation + Profiler) | | |
