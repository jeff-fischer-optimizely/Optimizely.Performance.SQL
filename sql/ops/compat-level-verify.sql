/*
    compat-level-verify.sql

    Run this IN THE CONTEXT OF THE CHANGED DATABASE after the raise. It reads only.

    Set @CutoverUtc to the UTC time the ALTER DATABASE ran. The script compares an equal
    window either side of it and reports:

        1. State       -- did the change actually take, and is the shim still in step?
        2. Regressions -- queries costing materially more after than before
        3. Wins        -- queries costing materially less
        4. Net         -- the totals, so a handful of loud regressions do not hide a win
        5. Plans       -- the pre-cutover plan_id to force if a regression needs rolling back

    Section 6 is a manual, pilot-only check on deferred table-variable compilation. It is
    commented out and explains itself in place.

    A note on what "cost" means here. Query Store's avg_logical_io_reads is the metric the
    triage corpus was built on, so it is the one used to compare. Duration is reported
    beside it because a plan can trade reads for CPU and you want to see that happen.

    Requires SQL Server 2016 / Azure SQL Database or later, with Query Store READ_WRITE
    across the whole comparison window. If Query Store was switched on at the same time as
    the raise, there is no baseline and this script cannot tell you anything.

    Runbook: docs/compatibility-level-remediation.md
*/

SET NOCOUNT ON;

DECLARE @CutoverUtc  datetime2(0) = '2026-01-01 00:00:00';  /* <-- SET THIS */
DECLARE @WindowHours int          = 168;                    /* 7 days either side */
DECLARE @MinExecutions bigint     = 100;                    /* ignore noise */
DECLARE @MaterialPct   float      = 20.0;                   /* report moves larger than this */

DECLARE @BeforeFrom datetime2(0) = DATEADD(hour, -@WindowHours, @CutoverUtc);
DECLARE @AfterTo    datetime2(0) = DATEADD(hour,  @WindowHours, @CutoverUtc);

------------------------------------------------------------------------------------------
-- 1. State
------------------------------------------------------------------------------------------
SELECT
    DatabaseName        = DB_NAME(),
    CompatibilityLevel  = CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel')),
    LegacyCE            = (SELECT CONVERT(bit, value) FROM sys.database_scoped_configurations WHERE name = 'LEGACY_CARDINALITY_ESTIMATION'),
    ParameterSniffing   = (SELECT CONVERT(bit, value) FROM sys.database_scoped_configurations WHERE name = 'PARAMETER_SNIFFING'),
    ScalarUdfInlining   = (SELECT CASE CONVERT(bit, value) WHEN 1 THEN 'ON' ELSE 'OFF' END FROM sys.database_scoped_configurations WHERE name = 'TSQL_SCALAR_UDF_INLINING'),
    QueryStore          = (SELECT actual_state_desc FROM sys.database_query_store_options),
    ForcedPlans         = (SELECT COUNT(*) FROM sys.query_store_plan WHERE is_forced_plan = 1),
    CutoverUtc          = @CutoverUtc,
    BaselineWindow      = CONVERT(nvarchar(30), @BeforeFrom) + N' .. ' + CONVERT(nvarchar(30), @CutoverUtc),
    ComparisonWindow    = CONVERT(nvarchar(30), @CutoverUtc)  + N' .. ' + CONVERT(nvarchar(30), @AfterTo),
    /*
        Reminder rather than a measurement: the shim probes compatibility level once per
        process and caches the answer for the lifetime of that process. If the application
        has not been recycled since the cutover it is still applying the level-140 gated
        rewrites against a level-150 database.
    */
    ShimRecycleRequired = N'Confirm the application was recycled after the cutover.';

------------------------------------------------------------------------------------------
-- Shared: per-query aggregates either side of the cutover
------------------------------------------------------------------------------------------
WITH Windowed AS
(
    SELECT
        q.query_id,
        Side = CASE WHEN rsi.start_time < @CutoverUtc THEN 'Before' ELSE 'After' END,
        rs.count_executions,
        rs.avg_logical_io_reads,
        rs.avg_duration,
        rs.avg_cpu_time
    FROM sys.query_store_runtime_stats            AS rs
    JOIN sys.query_store_runtime_stats_interval   AS rsi ON rsi.runtime_stats_interval_id = rs.runtime_stats_interval_id
    JOIN sys.query_store_plan                     AS p   ON p.plan_id  = rs.plan_id
    JOIN sys.query_store_query                    AS q   ON q.query_id = p.query_id
    WHERE rsi.start_time >= @BeforeFrom
      AND rsi.start_time <  @AfterTo
      AND rs.execution_type = 0   /* completed only; aborted and errored executions distort averages */
),
Agg AS
(
    SELECT
        query_id,
        ExecBefore   = SUM(CASE WHEN Side = 'Before' THEN count_executions ELSE 0 END),
        ExecAfter    = SUM(CASE WHEN Side = 'After'  THEN count_executions ELSE 0 END),
        /* Execution-weighted averages. An unweighted AVG over intervals over-counts quiet hours. */
        ReadsBefore  = SUM(CASE WHEN Side = 'Before' THEN avg_logical_io_reads * count_executions ELSE 0 END)
                       / NULLIF(SUM(CASE WHEN Side = 'Before' THEN count_executions ELSE 0 END), 0),
        ReadsAfter   = SUM(CASE WHEN Side = 'After'  THEN avg_logical_io_reads * count_executions ELSE 0 END)
                       / NULLIF(SUM(CASE WHEN Side = 'After'  THEN count_executions ELSE 0 END), 0),
        MsBefore     = SUM(CASE WHEN Side = 'Before' THEN avg_duration * count_executions ELSE 0 END)
                       / NULLIF(SUM(CASE WHEN Side = 'Before' THEN count_executions ELSE 0 END), 0) / 1000.0,
        MsAfter      = SUM(CASE WHEN Side = 'After'  THEN avg_duration * count_executions ELSE 0 END)
                       / NULLIF(SUM(CASE WHEN Side = 'After'  THEN count_executions ELSE 0 END), 0) / 1000.0,
        TotalReadsBefore = SUM(CASE WHEN Side = 'Before' THEN avg_logical_io_reads * count_executions ELSE 0 END),
        TotalReadsAfter  = SUM(CASE WHEN Side = 'After'  THEN avg_logical_io_reads * count_executions ELSE 0 END)
    FROM Windowed
    GROUP BY query_id
),
Compared AS
(
    SELECT
        a.*,
        ReadsPctChange = CASE WHEN a.ReadsBefore > 0
                              THEN (a.ReadsAfter - a.ReadsBefore) * 100.0 / a.ReadsBefore END,
        MsPctChange    = CASE WHEN a.MsBefore > 0
                              THEN (a.MsAfter - a.MsBefore) * 100.0 / a.MsBefore END,
        TotalReadsDelta = a.TotalReadsAfter - a.TotalReadsBefore
    FROM Agg AS a
    WHERE a.ExecBefore >= @MinExecutions
      AND a.ExecAfter  >= @MinExecutions
)
SELECT * INTO #Compared FROM Compared;

------------------------------------------------------------------------------------------
-- 2. Regressions, worst first, ranked by total reads added rather than by percentage.
--    A 900% regression on a query run twice an hour is not the thing to act on.
------------------------------------------------------------------------------------------
SELECT TOP (25)
    c.query_id,
    ObjectName      = ISNULL(OBJECT_NAME(q.object_id), N'(ad hoc)'),
    c.ExecBefore, c.ExecAfter,
    ReadsBefore     = CONVERT(decimal(18,1), c.ReadsBefore),
    ReadsAfter      = CONVERT(decimal(18,1), c.ReadsAfter),
    ReadsPctChange  = CONVERT(decimal(9,1),  c.ReadsPctChange),
    MsBefore        = CONVERT(decimal(18,1), c.MsBefore),
    MsAfter         = CONVERT(decimal(18,1), c.MsAfter),
    TotalReadsAdded = CONVERT(bigint, c.TotalReadsDelta),
    QueryText       = LEFT(qt.query_sql_text, 300),
    /* Tier 1 rollback for this specific query. Copy, fill in the plan_id, run. */
    ForcePlanHint   = N'EXEC sp_query_store_force_plan @query_id = ' + CONVERT(nvarchar(20), c.query_id)
                      + N', @plan_id = <pre-cutover plan_id from result set 5>;'
FROM   #Compared AS c
JOIN   sys.query_store_query      AS q  ON q.query_id = c.query_id
JOIN   sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
WHERE  c.ReadsPctChange > @MaterialPct
ORDER  BY c.TotalReadsDelta DESC;

------------------------------------------------------------------------------------------
-- 3. Wins, best first
------------------------------------------------------------------------------------------
SELECT TOP (25)
    c.query_id,
    ObjectName        = ISNULL(OBJECT_NAME(q.object_id), N'(ad hoc)'),
    c.ExecBefore, c.ExecAfter,
    ReadsBefore       = CONVERT(decimal(18,1), c.ReadsBefore),
    ReadsAfter        = CONVERT(decimal(18,1), c.ReadsAfter),
    ReadsPctChange    = CONVERT(decimal(9,1),  c.ReadsPctChange),
    MsBefore          = CONVERT(decimal(18,1), c.MsBefore),
    MsAfter           = CONVERT(decimal(18,1), c.MsAfter),
    TotalReadsSaved   = CONVERT(bigint, -c.TotalReadsDelta),
    QueryText         = LEFT(qt.query_sql_text, 300)
FROM   #Compared AS c
JOIN   sys.query_store_query      AS q  ON q.query_id = c.query_id
JOIN   sys.query_store_query_text AS qt ON qt.query_text_id = q.query_text_id
WHERE  c.ReadsPctChange < -@MaterialPct
ORDER  BY c.TotalReadsDelta ASC;

------------------------------------------------------------------------------------------
-- 4. Net. The number that decides whether the change stays.
------------------------------------------------------------------------------------------
SELECT
    QueriesCompared     = COUNT(*),
    QueriesRegressed    = SUM(CASE WHEN ReadsPctChange >  @MaterialPct THEN 1 ELSE 0 END),
    QueriesImproved     = SUM(CASE WHEN ReadsPctChange < -@MaterialPct THEN 1 ELSE 0 END),
    TotalReadsBefore    = CONVERT(bigint, SUM(TotalReadsBefore)),
    TotalReadsAfter     = CONVERT(bigint, SUM(TotalReadsAfter)),
    NetReadsPctChange   = CONVERT(decimal(9,1),
                            CASE WHEN SUM(TotalReadsBefore) > 0
                                 THEN (SUM(TotalReadsAfter) - SUM(TotalReadsBefore)) * 100.0 / SUM(TotalReadsBefore) END)
FROM #Compared;

------------------------------------------------------------------------------------------
-- 5. Plans per query for anything that regressed, so the pre-cutover plan_id to force
--    in a Tier 1 rollback can be read straight off.
------------------------------------------------------------------------------------------
SELECT
    c.query_id,
    p.plan_id,
    p.is_forced_plan,
    FirstSeenUtc = CONVERT(datetime2(0), p.initial_compile_start_time),
    Side         = CASE WHEN p.initial_compile_start_time < @CutoverUtc THEN 'Pre-cutover' ELSE 'Post-cutover' END,
    p.compatibility_level
FROM   #Compared AS c
JOIN   sys.query_store_plan AS p ON p.query_id = c.query_id
WHERE  c.ReadsPctChange > @MaterialPct
ORDER  BY c.TotalReadsDelta DESC, p.initial_compile_start_time;

DROP TABLE #Compared;

------------------------------------------------------------------------------------------
-- 6. Deferred table-variable compilation -- MANUAL, and only needed once per tranche.
--
--    Everything above is automated. This one is not, deliberately.
--
--    Reading an estimate out of a plan means finding that plan, and neither the plan cache
--    nor Query Store reliably retains a cheap ad-hoc probe: the cache may never keep the
--    inner sp_executesql batch, and Query Store's default AUTO capture mode discards
--    infrequent low-cost queries. A check that silently returns nothing is worse than no
--    check, so this is left as an interactive step.
--
--    It is also not needed per database. Deferred table-variable compilation is not
--    partial and not conditional: it is on at compatibility level 150 and off below it.
--    Result set 1 already confirms the level took. Run this once on each tranche's pilot
--    and take the answer as read for the rest of the wave.
--
--    HOW TO RUN IT
--      1. Enable "Include Actual Execution Plan" (Ctrl+M in SSMS).
--      2. Run the block below.
--      3. Open the plan for the statement marked TVPROBE and hover the Clustered Index
--         Scan of @tv.
--
--    Estimated Number of Rows should read 5000. If it reads 1, deferred compilation is not
--    engaged and the raise has not delivered the thing it was done for.
--
--    Measured on SQL Server 2022 while writing this runbook:
--
--        compatibility 140                              -> 1     (nested loops)
--        compatibility 150                              -> 5000  (hash match)
--        compatibility 150, LEGACY_CARDINALITY_ESTIMATION ON -> 5000
--
--    That third line is the one Tranche B depends on: pinning the legacy cardinality
--    estimator does not cost the database deferred table-variable compilation.
------------------------------------------------------------------------------------------
/*
DECLARE @tv TABLE (Id int NOT NULL PRIMARY KEY);

INSERT INTO @tv (Id)
SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b;

-- TVPROBE: the join is what makes this a non-trivial plan. A bare SELECT over @tv gets a
-- trivial plan, which skips deferred compilation and estimates 1 row at every level.
SELECT COUNT_BIG(*)
FROM @tv AS t
JOIN sys.all_columns AS c ON c.column_id = t.Id;
*/
