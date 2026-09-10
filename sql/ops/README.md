# Operational queries

Two read-only scripts supporting
[docs/compatibility-level-remediation.md](../../docs/compatibility-level-remediation.md).

Unlike everything else under [`sql/`](../README.md), these create nothing. They contain no
DDL, no `ALTER DATABASE`, and no writes of any kind — they read catalogue views, Query Store
and (in the commented-out section 6 of the verification script) run one throwaway `SELECT`
against a table variable. Safe to run in production during business hours.

| Script | When | What it answers |
| --- | --- | --- |
| [`compat-level-eligibility.sql`](compat-level-eligibility.sql) | Before the change | Which tranche is this database in, is anything blocking the raise, and what are the exact commands? |
| [`compat-level-verify.sql`](compat-level-verify.sql) | After the change | Did the change take, what regressed, what improved, and is the net a win? |

Run both **in the context of the database being assessed**, not `master`.

Both require SQL Server 2016 / Azure SQL Database or later. The verification script
additionally requires Query Store to have been `READ_WRITE` across the whole comparison
window — if Query Store was switched on at the same time as the raise there is no baseline
and the script cannot tell you anything.

Set `@CutoverUtc` in the verification script to the UTC time the `ALTER DATABASE` ran before
running it. Everything it reports keys off that value.
