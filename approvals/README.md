# Approval records

One file per rewrite, named by the entry it covers. `config/approved-sql.json` points at
these by path, and the `sql/` headers cite them by id.

## What lives here, and what lives in the SQL header

The header of a `sql/` script argues the change: this is what was wrong, this is what was
altered, this is why the rewrite returns the same rows. Anyone reading the procedure needs
that argument in front of them, so it stays with the procedure.

An approval record is the file the argument was *reviewed* in. It carries what the header
deliberately does not:

- what was actually checked, as opposed to what was reasoned about
- what would have to be true for the change to be wrong, stated so it can be tested
- what to do before enabling it, and how to turn it off
- the sign-off

The two overlap on the equivalence claim itself, and that repetition is intentional. A
reviewer should not have to open two files to know what they are approving.

## Status vocabulary

| Status | Means |
| --- | --- |
| **Catalogued** | Written and reviewed; `enabled: false` in configuration. The redirect exists on paper and cannot fire. |
| **Armed** | `enabled: true`. Will fire on any database that passes its gates. |
| **Field-verified** | Confirmed on a running Optimizely Foundation instance, with SQL Profiler showing the optimised text reaching the server and the application behaving unchanged. |

**Nothing in this directory is Field-verified yet.** The equivalence arguments have been
reviewed, all fifteen scripts parse clean against a live SQL Server, the highest-risk
rewrite has been differential-tested against its original, and the resolution logic has
been exercised across simulated database shapes. None of that is the acceptance test. The
acceptance test is Foundation, both CMS versions, Profiler attached.

Until an entry reaches Field-verified, read "approved" as *approved to test*, not
*approved to ship*.

## The constraint every record inherits

No script here alters, drops or replaces an object Optimizely ships. Each creates a new,
versioned object beside the original, and the shim rewrites the *call* at the client. That
is not a stylistic preference; it is the boundary the work was scoped inside, and a record
proposing to modify a shipped procedure would be rejected on that ground alone.

The shim never executes DDL. Applying `sql/*.sql` is a DBA action.

## Records

| Record | Entries | Risk | Status |
| --- | --- | --- | --- |
| [OPT-0001 — ecf_OrderSearch](OPT-0001-ecf-order-search.md) | OPT-0001 | Moderate | Catalogued |
| [OPT-0002 — netContentListPaged (CMS 12)](OPT-0002-net-content-list-paged.md) | OPT-0002 | Moderate | Catalogued |
| [OPT-0003 — editDeletePageCheckInternal](OPT-0003-edit-delete-page-check.md) | OPT-0003, OPT-0013 | Low | Armed |
| [OPT-0004 — netContentDataLoad](OPT-0004-net-content-data-load.md) | OPT-0004, OPT-0012 | Low | Armed |
| [OPT-0005 — ecf_CatalogEntry_Components](OPT-0005-catalog-entry-components.md) | OPT-0005, OPT-0014 | Low | Armed |
| [OPT-0006 — GetListPropertiesOverThreshold](OPT-0006-list-properties-over-threshold.md) | *(no redirect; callee of OPT-0002)* | Moderate | Catalogued |
| [OPT-0007 — ecf_NodeEntryRelations](OPT-0007-node-entry-relations.md) | OPT-0007 | Low | Armed |
| [OPT-0008 — ecf_CatalogEntry_List](OPT-0008-catalog-entry-list.md) | OPT-0008, OPT-0015 | Low | Armed |
| [OPT-0009 — netContentChildrenReferences](OPT-0009-net-content-children-references.md) | OPT-0009 | Low | Armed |
| [OPT-0010 — netContentListPaged (CMS 11)](OPT-0010-net-content-list-paged-cms11.md) | OPT-0010, OPT-0011 | Low | Armed |

Several records cover two entries. That is the usual case rather than the exception: the
same change against CMS 11 and CMS 12, or against Commerce 14 and 15, or split across two
disjoint compatibility windows, is one decision implemented twice. Reviewing it twice
would invite the two halves to drift apart.

The corpus these were drawn from, including the queries examined and deliberately left
alone, is in [docs/query-triage.md](../docs/query-triage.md).
