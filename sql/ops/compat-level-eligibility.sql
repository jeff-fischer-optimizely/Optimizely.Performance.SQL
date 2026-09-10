/*
    compat-level-eligibility.sql

    Run this IN THE CONTEXT OF THE DATABASE you are assessing. It reads only — it changes
    nothing — and answers three questions:

        1. Which remediation tranche is this database in?
        2. Is anything blocking the raise right now?
        3. What are the exact commands to run?

    Requires SQL Server 2016 / Azure SQL Database or later (sys.database_scoped_configurations,
    sys.database_query_store_options). The surveyed estate is uniformly Azure SQL Database
    12.0.2000.8, EngineEdition 5, so that holds fleet-wide.

    Companion: sql/ops/compat-level-verify.sql, run after the change.
    Runbook:   docs/compatibility-level-remediation.md
*/

SET NOCOUNT ON;

DECLARE @TargetLevel int = 150;   /* The runbook stops at 150 deliberately. See "Why 150". */

DECLARE @CurrentLevel   int             = CONVERT(int, DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel'));
DECLARE @Collation      nvarchar(256)   = CONVERT(nvarchar(256), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));
DECLARE @Updateability  nvarchar(128)   = CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Updateability'));
DECLARE @EngineEdition  int             = CONVERT(int, SERVERPROPERTY('EngineEdition'));
DECLARE @ProductVersion nvarchar(128)   = CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));

DECLARE @LegacyCe        bit = (SELECT CONVERT(bit, value)      FROM sys.database_scoped_configurations WHERE name = 'LEGACY_CARDINALITY_ESTIMATION');
DECLARE @ParamSniffing   bit = (SELECT CONVERT(bit, value)      FROM sys.database_scoped_configurations WHERE name = 'PARAMETER_SNIFFING');
DECLARE @UdfInlining     bit = (SELECT CONVERT(bit, value)      FROM sys.database_scoped_configurations WHERE name = 'TSQL_SCALAR_UDF_INLINING');

DECLARE @QsActual        nvarchar(60), @QsDesired nvarchar(60), @QsReadonlyReason bigint, @QsStaleDays bigint;
SELECT  @QsActual        = actual_state_desc,
        @QsDesired       = desired_state_desc,
        @QsReadonlyReason= readonly_reason,
        @QsStaleDays     = stale_query_threshold_days
FROM    sys.database_query_store_options;

DECLARE @ForcedPlans int =
    (SELECT COUNT(*) FROM sys.query_store_plan WHERE is_forced_plan = 1);

DECLARE @QsOldestUtc datetime2(0) =
    (SELECT CONVERT(datetime2(0), MIN(start_time)) FROM sys.query_store_runtime_stats_interval);

/* Which product schema is present. Determines which catalogue entries can be affected. */
DECLARE @HasCms      bit = CASE WHEN OBJECT_ID('dbo.tblContent', 'U')  IS NULL THEN 0 ELSE 1 END;
DECLARE @HasCommerce bit = CASE WHEN OBJECT_ID('dbo.CatalogEntry', 'U') IS NULL THEN 0 ELSE 1 END;

/*
    Scalar UDFs that SQL Server would inline at level 150. is_inlineable is authoritative
    and beats guessing from the body; it only exists on 2019+/Azure, hence the guard.
*/
DECLARE @ScalarUdfs int =
    (SELECT COUNT(*) FROM sys.objects WHERE type = 'FN' AND is_ms_shipped = 0);
DECLARE @InlineableUdfs int = NULL;

IF COL_LENGTH('sys.sql_modules', 'is_inlineable') IS NOT NULL
BEGIN
    DECLARE @c int;
    EXEC sp_executesql
        N'SELECT @c = COUNT(*)
          FROM sys.sql_modules m
          JOIN sys.objects o ON o.object_id = m.object_id
          WHERE o.type = ''FN'' AND o.is_ms_shipped = 0 AND m.is_inlineable = 1;',
        N'@c int OUTPUT', @c = @c OUTPUT;
    SET @InlineableUdfs = @c;
END

DECLARE @Tranche nvarchar(20) =
    CASE
        WHEN @CurrentLevel >= @TargetLevel                  THEN N'None (already 150+)'
        WHEN @CurrentLevel BETWEEN 120 AND 140              THEN N'A'
        WHEN @CurrentLevel IN (100, 110)                    THEN N'B'
        ELSE                                                     N'Review'
    END;

------------------------------------------------------------------------------------------
-- Result set 1: the assessment
------------------------------------------------------------------------------------------
SELECT
    DatabaseName        = DB_NAME(),
    Tranche             = @Tranche,
    CurrentLevel        = @CurrentLevel,
    TargetLevel         = @TargetLevel,
    Product             = CASE WHEN @HasCms = 1 AND @HasCommerce = 1 THEN N'CMS + Commerce'
                               WHEN @HasCms = 1                      THEN N'CMS'
                               WHEN @HasCommerce = 1                 THEN N'Commerce'
                               ELSE N'Neither (not an Optimizely database?)' END,
    LegacyCE            = @LegacyCe,
    ParameterSniffing   = @ParamSniffing,
    ScalarUdfInlining   = CASE @UdfInlining WHEN 1 THEN N'ON' WHEN 0 THEN N'OFF' ELSE N'(not supported)' END,
    ScalarUdfs          = @ScalarUdfs,
    InlineableScalarUdfs= @InlineableUdfs,
    QueryStore          = @QsActual,
    QueryStoreOldestUtc = @QsOldestUtc,
    ForcedPlans         = @ForcedPlans,
    Collation           = @Collation,
    EngineEdition       = @EngineEdition,
    ProductVersion      = @ProductVersion,

    ApplyCommand =
        CASE @Tranche
            WHEN N'A' THEN
                N'ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = ' + CONVERT(nvarchar(10), @TargetLevel) + N';'
            WHEN N'B' THEN
                N'ALTER DATABASE SCOPED CONFIGURATION SET LEGACY_CARDINALITY_ESTIMATION = ON;' + CHAR(13) + CHAR(10) +
                N'ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = ' + CONVERT(nvarchar(10), @TargetLevel) + N';'
            ELSE N'-- nothing to apply'
        END,

    RollbackCommand =
        CASE WHEN @Tranche IN (N'A', N'B')
             THEN N'ALTER DATABASE CURRENT SET COMPATIBILITY_LEVEL = ' + CONVERT(nvarchar(10), @CurrentLevel) + N';'
             ELSE N'-- nothing to roll back'
        END,

    /* Recorded so the rollback command above can be reconstructed later from the run log. */
    PreChangeLevel = @CurrentLevel;

------------------------------------------------------------------------------------------
-- Result set 2: blockers and warnings. Empty means clear to proceed.
------------------------------------------------------------------------------------------
DECLARE @Findings TABLE (Severity nvarchar(10), Finding nvarchar(200), Detail nvarchar(600));

IF @Tranche = N'Review'
    INSERT @Findings VALUES (N'BLOCKER',
        N'Compatibility level is not one this runbook classifies',
        N'Level ' + CONVERT(nvarchar(10), @CurrentLevel) + N' falls outside 100-140 and is below the 150 target. Assess by hand.');

IF @Updateability <> N'READ_WRITE'
    INSERT @Findings VALUES (N'BLOCKER',
        N'Database is not writable',
        N'Updateability = ' + ISNULL(@Updateability, N'(unknown)') + N'. ALTER DATABASE will fail. This is normally a readable secondary; run against the primary.');

IF @QsActual IS NULL OR @QsActual <> N'READ_WRITE'
    INSERT @Findings VALUES (N'BLOCKER',
        N'Query Store is not READ_WRITE',
        N'actual_state_desc = ' + ISNULL(@QsActual, N'(none)') + N', desired = ' + ISNULL(@QsDesired, N'(none)')
        + N', readonly_reason = ' + ISNULL(CONVERT(nvarchar(20), @QsReadonlyReason), N'(none)')
        + N'. Without Query Store there is no before/after evidence and no plan-forcing rollback. Fix first: ALTER DATABASE CURRENT SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE);');

IF @QsActual = N'READ_WRITE' AND @QsStaleDays IS NOT NULL AND @QsStaleDays < 8
    INSERT @Findings VALUES (N'WARNING',
        N'Query Store retention is shorter than the watch window',
        N'stale_query_threshold_days = ' + CONVERT(nvarchar(20), @QsStaleDays)
        + N'. The runbook watches for 7 days; history will age out mid-comparison. Raise to at least 14 before the change.');

IF @QsActual = N'READ_WRITE' AND (@QsOldestUtc IS NULL OR @QsOldestUtc > DATEADD(day, -3, SYSUTCDATETIME()))
    INSERT @Findings VALUES (N'WARNING',
        N'Query Store has less than three days of history',
        N'Oldest interval: ' + ISNULL(CONVERT(nvarchar(30), @QsOldestUtc), N'(none)')
        + N'. The baseline will be thin and weekly workload will not be represented. Let it accumulate before the change.');

IF @ForcedPlans > 0
    INSERT @Findings VALUES (N'WARNING',
        N'Forced plans are present',
        CONVERT(nvarchar(10), @ForcedPlans) + N' forced plan(s). A forced plan survives the level change and will keep the old plan in place, so those queries will not improve and will make the before/after read as "no change". Review whether each is still needed.');

IF @Tranche = N'A' AND @LegacyCe = 1
    INSERT @Findings VALUES (N'NOTE',
        N'Legacy cardinality estimation is already pinned ON',
        N'The raise will not change the CE for this database, which removes the largest source of plan movement. It still enables deferred table-variable compilation and scalar UDF inlining, which are the point.');

IF @Tranche = N'B' AND @LegacyCe = 1
    INSERT @Findings VALUES (N'NOTE',
        N'Legacy cardinality estimation is already pinned ON',
        N'The first line of the Tranche B apply command is therefore a no-op. Run it anyway; it makes the run log self-describing.');

IF @ParamSniffing = 0
    INSERT @Findings VALUES (N'NOTE',
        N'PARAMETER_SNIFFING is OFF on this database',
        N'Non-default. Someone turned it off for a reason. Find out what that reason was before changing optimiser behaviour underneath it.');

IF @InlineableUdfs > 0
    INSERT @Findings VALUES (N'NOTE',
        N'Scalar UDFs that level 150 will inline',
        CONVERT(nvarchar(10), @InlineableUdfs) + N' of ' + CONVERT(nvarchar(10), @ScalarUdfs)
        + N' scalar function(s) report is_inlineable = 1. Inlining is usually a win. If it is not, the mitigation is ALTER DATABASE SCOPED CONFIGURATION SET TSQL_SCALAR_UDF_INLINING = OFF -- not a level revert.');

IF @InlineableUdfs IS NULL AND @ScalarUdfs > 0
    INSERT @Findings VALUES (N'NOTE',
        N'Cannot determine scalar UDF inlineability',
        N'sys.sql_modules.is_inlineable is not available on this build. ' + CONVERT(nvarchar(10), @ScalarUdfs)
        + N' scalar function(s) exist; assume some will inline at 150.');

IF @UdfInlining = 0
    INSERT @Findings VALUES (N'NOTE',
        N'Scalar UDF inlining is already disabled at database scope',
        N'TSQL_SCALAR_UDF_INLINING = OFF. That mitigation is already spent; it is not available as a rollback lever here.');

IF @HasCms = 0 AND @HasCommerce = 0
    INSERT @Findings VALUES (N'WARNING',
        N'No Optimizely CMS or Commerce schema found',
        N'Neither dbo.tblContent nor dbo.CatalogEntry exists. Confirm you are connected to the right database.');

SELECT Severity, Finding, Detail
FROM   @Findings
ORDER  BY CASE Severity WHEN N'BLOCKER' THEN 1 WHEN N'WARNING' THEN 2 ELSE 3 END, Finding;

------------------------------------------------------------------------------------------
-- Result set 3: which approved-SQL catalogue entries change behaviour at the target level
------------------------------------------------------------------------------------------
SELECT Entry, Product, Gate, EffectOfTheRaise
FROM (VALUES
    (N'OPT-0002', N'CMS 12',      N'max 140', N'Stops firing. The recompile hints it exists to apply become redundant.'),
    (N'OPT-0003', N'CMS 12',      N'max 140', N'Stops firing.'),
    (N'OPT-0013', N'CMS 11',      N'max 140', N'Stops firing.'),
    (N'OPT-0011', N'CMS 11',      N'max 140', N'Stops firing, and OPT-0010 takes over. See the row below.'),
    (N'OPT-0010', N'CMS 11',      N'min 150', N'STARTS firing. Deploy netContentListPaged_cms11_optiperf_v1 before the raise, or the redirect is skipped and the site silently loses the set-based unpack.'),
    (N'OPT-0005', N'Commerce 15', N'max 140', N'Stops firing.'),
    (N'OPT-0014', N'Commerce 14', N'max 140', N'Stops firing.'),
    (N'OPT-0008', N'Commerce 15', N'max 140', N'Stops firing.'),
    (N'OPT-0015', N'Commerce 14', N'max 140', N'Stops firing.'),
    (N'OPT-0007', N'Commerce',    N'max 140', N'Stops firing.'),
    (N'OPT-0001', N'Commerce',    N'none',    N'Unaffected (and currently disabled).'),
    (N'OPT-0004', N'CMS 12',      N'none',    N'Unaffected.'),
    (N'OPT-0012', N'CMS 11',      N'none',    N'Unaffected.'),
    (N'OPT-0009', N'CMS',         N'collation', N'Unaffected.')
) AS c(Entry, Product, Gate, EffectOfTheRaise)
WHERE (@HasCms = 1      AND Product LIKE N'CMS%')
   OR (@HasCommerce = 1 AND Product LIKE N'Commerce%')
ORDER BY Entry;
