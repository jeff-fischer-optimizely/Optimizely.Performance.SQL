/*
    dbo.ecf_OrderSearch_optiperf_v1        OPT-0001        risk: Moderate

    Derived from EPiServer.Commerce.Core 15.1.0 / 14.46.0 (identical body in both).

    The largest single item in the survey: 75 databases, and roughly an eighth of all fleet
    logical reads attributed to one procedure. Every order search in Commerce goes through
    it, via ecf_Search_PurchaseOrder, ecf_Search_ShoppingCart and ecf_Search_PaymentPlan.

    THE PROBLEM
    -----------
    The procedure cursors over the order meta classes and concatenates a UNION ALL of
    "select ObjectId from <metaclass table>" across all of them. It then builds:

        SELECT OrderGroupId
        FROM dbo.OrderGroup OrderGroup
        INNER JOIN (select distinct U.[Key] from ( <the union> ) U) META
            ON OrderGroup.[OrderGroupId] = META.[Key]
        WHERE 1=1 AND ( <caller's filter> )
        ORDER BY ... OFFSET ... FETCH NEXT ...

    The derived table is a DISTINCT over the concatenation of every row of every order
    metaclass table. Written that way it is an independent subtree: the optimiser's
    reasonable move is to materialise it -- read every metaclass table in full, sort or
    hash the union to apply DISTINCT -- and only then join it to OrderGroup and apply the
    caller's filter.

    So the cost of a search scales with the size of the entire order history, not with the
    number of orders that match. On a store with millions of orders, searching for one
    order number reads the whole meta estate to find it. The count query built alongside it
    does the same work a second time whenever @ReturnTotalCount is set.

    WHAT CHANGED
    ------------
    The INNER JOIN to the DISTINCT derived table becomes a WHERE EXISTS semi-join:

        WHERE EXISTS (SELECT 1 FROM ( <the union> ) U WHERE U.[Key] = OrderGroup.OrderGroupId)

    That is all. The cursor, the metaclass enumeration, the union text, the caller's filter,
    the ORDER BY, the paging clause and the count query are untouched.

    WHY THE RESULT SET IS UNCHANGED
    -------------------------------
    Three facts, and the argument is complete:

      1. Nothing in the outer query references a column of META. The select list is
         OrderGroupId, and the count query is COUNT(1). So the join contributes no data --
         only a filter.

      2. The derived table is DISTINCT on the single column being joined. An inner join to a
         distinct key list matches each outer row at most once, so it can neither duplicate
         nor drop rows relative to an existence test.

      3. Therefore "join to distinct keys, project nothing from them" and "exists" select
         precisely the same rows of OrderGroup, in the same multiplicity.

    With EXISTS, the correlation gives the optimiser what the derived table hid: it can
    drive from OrderGroup, apply the caller's filter and the ORDER BY first, and probe the
    metaclass tables only for surviving rows -- and short-circuit on the first hit rather
    than materialising a distinct set it never needed.

    WHY MODERATE AND NOT LOW
    ------------------------
    The result set argument above holds for every input. The plan does not.

    This changes plan shape for the single most heavily used query in Commerce, on a
    statement assembled at runtime from caller-supplied text, which means the shapes in
    production are not enumerable from here. A semi-join is the right default -- it lets the
    filter go first -- but "right default" is not "never worse", and a filter that matches
    most of the table can make a per-row probe cost more than the one-time materialisation
    it replaced.

    That is exactly the Moderate tier: provably the same rows, materially different plan,
    wants measurement on real data before it leaves shadow mode. Run it in shadow first,
    on the store with the largest OrderGroup, and compare.

    NOT DONE HERE
    -------------
    @SQLClause, @MetaSQLClause and @OrderBy are concatenated into the statement
    unparameterised. That costs a plan cache entry per distinct search, and it is an
    injection surface. Both are real problems and neither is ours: fixing them means
    changing the contract with the calling assembly, which is Optimizely's to change.
    Recorded in approvals/OPT-0001 and reported upstream rather than patched here.
*/

IF OBJECT_ID('dbo.ecf_OrderSearch_optiperf_v1', 'P') IS NOT NULL
    DROP PROCEDURE dbo.ecf_OrderSearch_optiperf_v1;
GO

CREATE PROCEDURE [dbo].[ecf_OrderSearch_optiperf_v1]
(
	@SQLClause 					nvarchar(max),
	@MetaSQLClause 				nvarchar(max),
	@OrderBy 					nvarchar(max),
	@Namespace					nvarchar(1024) = N'',
	@Classes					nvarchar(max) = N'',
	@StartingRec 				int,
	@NumRecords   				int,
	@RecordCount                int OUTPUT,
	@ReturnTotalCount			bit = 1
)
AS
BEGIN
	SET NOCOUNT ON

	DECLARE @query_tmp nvarchar(max)
	DECLARE @FilterQuery_tmp nvarchar(max)
	DECLARE @TableName_tmp sysname
	DECLARE @SelectMetaQuery_tmp nvarchar(max)
	DECLARE @WhereMetaExists_tmp nvarchar(max)
	DECLARE @FullQuery nvarchar(max)
	DECLARE @SelectQuery nvarchar(max)
	DECLARE @CountQuery nvarchar(max)

	-- 1. Cycle through all the available product meta classes
	DECLARE MetaClassCursor CURSOR READ_ONLY
	FOR SELECT TableName FROM MetaClass
		WHERE Namespace like @Namespace + '%' AND ([Name] in (select Item from ecf_splitlist(@Classes)) or @Classes = '')
		and IsSystem = 0

	OPEN MetaClassCursor
	FETCH NEXT FROM MetaClassCursor INTO @TableName_tmp
	WHILE (@@fetch_status = 0)
	BEGIN
		set @Query_tmp = 'select META.ObjectId as ''Key'' from ' + @TableName_tmp + ' META'

		-- Add meta Where clause
		if(LEN(@MetaSQLClause)>0)
			set @query_tmp = @query_tmp + ' WHERE ' + @MetaSQLClause

		if(@SelectMetaQuery_tmp is null)
			set @SelectMetaQuery_tmp = @Query_tmp;
		else
			set @SelectMetaQuery_tmp = @SelectMetaQuery_tmp + N' UNION ALL ' + @Query_tmp;

	FETCH NEXT FROM MetaClassCursor INTO @TableName_tmp
	END
	CLOSE MetaClassCursor
	DEALLOCATE MetaClassCursor

	/* OPTIPERF: the original built
	       INNER JOIN (select distinct U.[Key] from ( ... ) U) META ON OrderGroup.[OrderGroupId] = META.[Key]
	   which is an uncorrelated derived table the optimiser materialises in full before it
	   can apply the caller's filter. The correlated existence test selects the same rows --
	   nothing projects from META, and the join was to a DISTINCT key -- but lets the filter
	   and the paging run first. */
	SET @WhereMetaExists_tmp = N' AND EXISTS (SELECT 1 FROM (' + @SelectMetaQuery_tmp + N') U WHERE U.[Key] = OrderGroup.[OrderGroupId]) '

	set @FilterQuery_tmp = N' WHERE 1=1'
	-- add sql clause statement here, if specified
	if(Len(@SQLClause) != 0)
		set @FilterQuery_tmp = @FilterQuery_tmp + N' AND (' + @SqlClause + ')'

	set @FilterQuery_tmp = @FilterQuery_tmp + @WhereMetaExists_tmp

	if(Len(@OrderBy) = 0)
	begin
		set @OrderBy = ' OrderGroupId DESC'
	end

	set @SelectQuery = N'SELECT OrderGroupId'  +
		' FROM dbo.OrderGroup OrderGroup ' + @FilterQuery_tmp + ' ORDER BY ' + @OrderBy +
		' OFFSET '  + cast(@StartingRec as nvarchar(50)) + '  ROWS ' +
		' FETCH NEXT ' + cast(@NumRecords as nvarchar(50)) + ' ROWS ONLY ;';
	set @CountQuery= N'SET @RecordCount= (SELECT Count(1) FROM dbo.OrderGroup OrderGroup ' + @FilterQuery_tmp +');';

	IF (@NumRecords = 0)
	BEGIN
		set @FullQuery =  @CountQuery
	END
	ELSE IF (@ReturnTotalCount = 1)
	BEGIN
		set @FullQuery =  @CountQuery+ @SelectQuery;
	END
	ELSE
	BEGIN
		set @FullQuery =  @SelectQuery;
	END

	exec sp_executesql @FullQuery, N'@RecordCount int output', @RecordCount = @RecordCount OUTPUT

	SET NOCOUNT OFF
END
GO
